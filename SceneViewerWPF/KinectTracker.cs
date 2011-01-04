﻿using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SlimDX;
using xn;

namespace SceneViewerWPF
{
    public class KinectTracker : IDisposable
    {
        private Context _niContext;
        private ImageGenerator _imageNode;
        private ImageMetaData _imageMeta;
        private DepthGenerator _depthNode;
        private DepthMetaData _depthMeta;
        private SceneAnalyzer _sceneNode;
        private SceneMetaData _sceneMeta;

        private AsyncStateData _currentState;
        private KinectFrame _currentFrame;

        private KinectCameraInfo _cameraInfo;
        private UserGenerator _userGenerator;

        public KinectCameraInfo CameraInfo
        {
            get { return _cameraInfo; }
        }

        public KinectFrame CurrentFrame
        {
            //TODO: guard that this data is only accessed on the dispather thread
            get { return _currentFrame; }
        }

        private class AsyncStateData
        {
            public readonly AsyncOperation AsyncOperation;
            public volatile bool Canceled = false;
            public volatile bool Running = true;

            public AsyncStateData(object stateData)
            {
                AsyncOperation = AsyncOperationManager.CreateOperation(stateData);
            }
        }

        public event EventHandler TrackinkgStarted;

        public event EventHandler TrackinkgCompleted;

        public event EventHandler TrackingUpdated;

        protected void InvokeTrackinkgStarted(EventArgs e)
        {
            EventHandler handler = TrackinkgStarted;
            if (handler != null) handler(this, e);
        }

        protected void InvokeTrackingUpdated(EventArgs e)
        {
            EventHandler handler = TrackingUpdated;
            if (handler != null) handler(this, e);
        }

        protected void InvokeTrackinkgCompleted(EventArgs e)
        {
            EventHandler handler = TrackinkgCompleted;
            if (handler != null) handler(this, e);
        }
        public void StartTracking()
        {
            StopTracking();

            var asyncData = new AsyncStateData(new object());

            TrackDelegate trackDelegate = Track;
            trackDelegate.BeginInvoke(asyncData, trackDelegate.EndInvoke, null);

            _currentState = asyncData;
        }

        public void StopTracking()
        {
            if (_currentState != null && _currentState.Running)
                _currentState.Canceled = true;
        }

        private delegate void TrackDelegate(AsyncStateData asyncData);

        private void Track(AsyncStateData asyncData)
        {
            asyncData.Running = true;

            if (InitOpenNi(asyncData))
            {
                while (!asyncData.Canceled)
                {
                    _niContext.WaitAndUpdateAll();

                    _imageNode.GetMetaData(_imageMeta);
                    _depthNode.GetMetaData(_depthMeta);
                    //_sceneNode.GetMetaData(_sceneMeta);

                    asyncData.AsyncOperation.SynchronizationContext.Send(
                        delegate
                        {
                            //UpdateCameraInfo();
                            UpdateFrameData();
                            InvokeTrackingUpdated(EventArgs.Empty);
                        }, null);

                }
            }

            _userGenerator.StopGenerating();
            
            //TODO: this call causes exception in Dispose
            //_niContext.Shutdown();

            asyncData.Running = false;
            asyncData.AsyncOperation.PostOperationCompleted(evt => InvokeTrackinkgCompleted(EventArgs.Empty), null);

        }

        private bool InitOpenNi(AsyncStateData asyncData)
        {
            try
            {
                _niContext = new Context("openni.xml");

                _imageNode = (ImageGenerator)_niContext.FindExistingNode(NodeType.Image);
                _imageMeta = new ImageMetaData();
                _imageNode.GetMetaData(_imageMeta);

                if (_imageMeta.PixelFormat != PixelFormat.RGB24)
                    throw new InvalidOperationException("Only RGB24 pixel format is supported");

                // add depth node
                _depthNode = _niContext.FindExistingNode(NodeType.Depth) as DepthGenerator;
                if (_depthNode == null)
                {
                    throw new InvalidOperationException("Viewer must have a depth node!");
                }
                _depthMeta = new DepthMetaData();
                _depthNode.GetMetaData(_depthMeta);

                if (_depthMeta.PixelFormat != PixelFormat.Grayscale16Bit)
                    throw new InvalidOperationException("Only 16-bit depth precission is supported");

                if (_depthMeta.XRes != _imageMeta.XRes || _depthMeta.YRes != _imageMeta.YRes)
                    throw new InvalidOperationException("Image and depth map must have the same resolution");

                // add scene node
/*
                _sceneNode = (SceneAnalyzer)_niContext.FindExistingNode(NodeType.Scene);
                _sceneMeta = new SceneMetaData();
                //_sceneNode.GetMetaData(_sceneMeta);
*/
                // add user generator
                _userGenerator = new UserGenerator(_niContext);
                _userGenerator.NewUser += UserGenerator_NewUser;
                _userGenerator.LostUser += UserGenerator_LostUser;
                //_userGenerator.StartGenerating();

                // initialize buffers
                asyncData.AsyncOperation.SynchronizationContext.Send(
                    delegate
                    {
                        UpdateCameraInfo();
                        UpdateFrameData();
                        InvokeTrackinkgStarted(EventArgs.Empty);
                    }, null);

                _niContext.StartGeneratingAll();

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        void UserGenerator_LostUser(ProductionNode node, uint id)
        {
            Debug.Print("User lost {0}", id);

            EnsureFrame();

            CurrentFrame.Users.Remove((int)id);
        }

        void UserGenerator_NewUser(ProductionNode node, uint id)
        {
            Debug.Print("User found {0}", id);

            EnsureFrame();

            CurrentFrame.Users.Add(new KinectUserInfo { Id = (int) id});
        }

        private void UpdateFrameData()
        {
            EnsureFrame();

            _currentFrame.FrameId = (int) _imageMeta.FrameID;

            int imageSize = _imageMeta.XRes*_imageMeta.YRes*3;
            Debug.Assert(imageSize == _imageMeta.DataSize);

            if (_currentFrame.ImageMap == null || _currentFrame.ImageMap.Length != imageSize)
                _currentFrame.ImageMap = new byte[imageSize];

            // copy image data
            Marshal.Copy(_imageMeta.ImageMapPtr, _currentFrame.ImageMap, 0, imageSize);

            int depthSize = _depthMeta.XRes*_depthMeta.YRes;
            Debug.Assert(depthSize*sizeof (ushort) == _depthMeta.DataSize);

            if (_currentFrame.DepthMap == null || _currentFrame.DepthMap.Length != depthSize)
                _currentFrame.DepthMap = new short[depthSize];

            // copy depth data
            Marshal.Copy(_depthMeta.DepthMapPtr, _currentFrame.DepthMap, 0, depthSize);

            //TODO: the scene meta should be read from SceneAnalyzer instead of UserGenerator
            _sceneMeta = _userGenerator.GetUserPixels(0);
            int sceneSize = _sceneMeta.XRes*_sceneMeta.YRes;
            Debug.Assert(sceneSize*sizeof (ushort) == _sceneMeta.DataSize);

            if (_currentFrame.SceneMap == null || _currentFrame.SceneMap.Length != sceneSize)
                _currentFrame.SceneMap = new short[sceneSize];

            // copy scene data (user labels)
            Marshal.Copy(_sceneMeta.SceneMapPtr, _currentFrame.SceneMap, 0, sceneSize);

            var userIds = _userGenerator.GetUsers();
            foreach (var user in CurrentFrame.Users)
            {
                Debug.Assert(userIds.Contains((uint)user.Id));

                user.CenterOfMass = _userGenerator.GetCoM((uint) user.Id);
            }
        }

        private void EnsureFrame()
        {
            if (_currentFrame == null)
            {
                _currentFrame = new KinectFrame();
            }
        }

        private void UpdateCameraInfo()
        {
            if (_cameraInfo == null)
                _cameraInfo = new KinectCameraInfo();

            _cameraInfo.XRes = _imageMeta.XRes;
            _cameraInfo.YRes = _imageMeta.YRes;
            _cameraInfo.ZRes = _depthMeta.ZRes;

            // get the focal length in mm (ZPS = zero plane distance)/ focal length
            //  _imageCameraInfo.ZeroPlaneDistance = _imageNode.GetIntProperty("ZPD");
            _cameraInfo.ZeroPlaneDistance = _depthNode.GetIntProperty("ZPD");

            // get the pixel size in mm ("ZPPS" = pixel size at zero plane) 
            // _imageCameraInfo.ZeroPlanePixelSize = _imageNode.GetRealProperty("ZPPS") * 2.0;
            _cameraInfo.ZeroPlanePixelSize = _depthNode.GetRealProperty("ZPPS")*2.0;

            _cameraInfo.FocalLengthImage = 525f;
            _cameraInfo.FocalLengthDetph = _cameraInfo.ZeroPlaneDistance/_cameraInfo.ZeroPlanePixelSize;

            // get base line (distance from IR camera to laser projector in cm)
            _cameraInfo.Baseline = _depthNode.GetRealProperty("LDDIS")*10;
            _cameraInfo.ShadowValue = _depthNode.GetIntProperty("ShadowValue");
            _cameraInfo.NoSampleValue = _depthNode.GetIntProperty("NoSampleValue");

            // best guess
            _cameraInfo.DepthToRgb  = Matrix.Translation(35f, 15f, 0f);

/*
            //from ROS calibraition
            _cameraInfo.DepthToRgb = new Matrix
                                         {
                                            M11 =  1f,        M21 = 0.006497f, M31 = -0.000801f, M41 = -25.165f,  
                                            M12 = -0.006498f, M22 = 1f,        M32 = -0.001054f, M42 = 0.047f, 
                                            M13 =  0.000794f, M23 = 0.001059f, M33 =  1f,        M43 = -4.077f,
                                            M14 =  0f,        M24 = 0f,        M34 =  0f,        M44 = 1f
                                         };
*/
        }

        public void Dispose()
        {
            StopTracking();

            if (_userGenerator != null)
                _userGenerator.Dispose();

            if (_niContext != null)
                _niContext.Dispose();
        }
    }

    public class KinectFrame
    {
        public int FrameId { get; set; }

        public byte[] ImageMap { get; set; }
        public short[] DepthMap { get; set; }
        public short[] SceneMap { get; set; }

        public KinectUserCollection Users { get; private set; }

        public KinectFrame()
        {
            Users = new KinectUserCollection();
        }
    }

    public class KinectUserInfo
    {
        public int Id { get; set; }
        public bool IsTracking { get; set; }
        public bool IsCalibrating { get; set; }
        public Point3D CenterOfMass { get; set; }
    }

    public class KinectUserCollection : KeyedCollection<int,KinectUserInfo>
    {
        protected override int GetKeyForItem(KinectUserInfo item)
        {
            return item.Id;
        }
    }

    public class KinectCameraInfo
    {
        public int XRes { get; set; }
        public int YRes { get; set; }
        public int ZRes { get; set; }
        public double ZeroPlaneDistance { get; set; }
        public double ZeroPlanePixelSize { get; set; }

        public double FocalLengthDetph { get; set; }
        public double FocalLengthImage { get; set; }
        
        public double Baseline { get; set; }
        public ulong ShadowValue { get; set; }
        public ulong NoSampleValue { get; set; }

        public Matrix DepthToRgb { get; set; }
    }
}
