﻿using System;
using Windows.UI.Xaml.Controls;
using Windows.Devices.Sensors;
using Windows.Graphics.Display;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.System.Display;
using Windows.UI.Xaml;
using Windows.UI.Core;
using System.Diagnostics;
using Windows.Devices.Enumeration;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using System.Collections.Generic;
using Windows.Media.FaceAnalysis;
using Windows.UI;
using Windows.Media;
using System.Threading;
using Windows.System.Threading;
using System.Collections.ObjectModel;
using Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Emotion.Contract;

namespace FaceDetection.Controls
{
    // The usercontrol implementation first displays Camera selection list
    // and the face detection can be started by clicking the list for selected camera
    // in the beginning we are using FaceTracker for detecing faces
    // once we got detection, we ighlight the detected face with rectangle drawn on top of it
    // Note that FaceTracker appears to like Nv12 pixelformat and Face API does not work with it
    // Thus we need to take another preview frame and pass it to the FaceMetaData for processing
    // once done, we get array of FaceWithEmotions objects, 
    // which we simply pass to the DataSender to be delivered to our HTTP proxy
    // note that we constantly try giving new images to the FaceMetaData
    // the max-rate is determined by _frameProcessingTimer timeout time
    // However the FaceMetaData has its own internal logic to determine when it can start processing
    // and if it is not on the right state, the image is simply ignored

    public sealed partial class FaceDetector : UserControl
    {
        private DataSender _dataSender;
        private FaceMetaData _faceMetaData;
        private FaceTracker _faceTracker;
        private ThreadPoolTimer _frameProcessingTimer;
        private SemaphoreSlim _frameProcessingSemaphore = new SemaphoreSlim(1);
        
        private uint _videoWidth;
        private uint _videoHeight;

        public ObservableCollection<FaceDetails> FaceCollection
        {
            get;
            set;
        }

        // Receive notifications about rotation of the device and UI and apply any necessary rotation to the preview stream and UI controls
        private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();
        private readonly SimpleOrientationSensor _orientationSensor = SimpleOrientationSensor.GetDefault();
        private SimpleOrientation _deviceOrientation = SimpleOrientation.NotRotated;
        private DisplayOrientations _displayOrientation = DisplayOrientations.Portrait;

        // Prevent the screen from sleeping while the camera is running
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // MediaCapture and its state variables
        private MediaCapture _mediaCapture;
        private bool _isCaptureInitialized;
        private string _faceKey;
        private string _emotionKey;

        private IMediaEncodingProperties _previewProperties;

        // Information about the camera device
        private bool _mirroringPreview;
        private bool _externalCamera;

        // Rotation metadata to apply to the preview stream and recorded videos (MF_MT_VIDEO_ROTATION)
        // Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        public FaceDetector()
        {
            this.InitializeComponent();
            FaceCollection = new ObservableCollection<FaceDetails>();
            FaceListBox.ItemsSource = FaceCollection;
        }

        /// <summary>
        /// Initializes the MediaCapture, registers events, gets camera device information for mirroring and rotating, starts preview and unlocks the UI
        /// </summary>
        /// <returns></returns>
        public async Task Initialize(string faceKey, string emotionKey)
        {
            Debug.WriteLine("Initialize-Facedetector");

            _faceKey = faceKey;
            _emotionKey = emotionKey;

            _faceMetaData = new FaceMetaData(_faceKey, _emotionKey);
            _faceMetaData.DetectedFaces += FaceMetaData_DetectedFaces;

            _displayOrientation = _displayInformation.CurrentOrientation;
            if (_orientationSensor != null)
            {
                _deviceOrientation = _orientationSensor.GetCurrentOrientation();
            }

            // Clear any rectangles that may have been left over from a previous instance of the effect
            FacesCanvas.Children.Clear();

            DeviceInformationCollection allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            CameraListBox.ItemsSource = allVideoDevices;
        }

        private async void CameraSelectionListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            DeviceInformation SelectedItem = (DeviceInformation)e.ClickedItem;
            cameraSelectionList.Visibility = Visibility.Collapsed;
            await InitMediaCapture(SelectedItem);
        }
        private async Task InitMediaCapture(DeviceInformation cameraDevice)
        {
            if (_mediaCapture != null || cameraDevice == null)
            {
                return;//Already initialized
            }

            //var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Front, "Microsoft");

            if (cameraDevice == null)
            {
                Debug.WriteLine("No camera device found!");
                return;
            }

            // Create MediaCapture and its settings
            _mediaCapture = new MediaCapture();

            MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

            settings.StreamingCaptureMode = StreamingCaptureMode.Video;

            _mediaCapture.Failed += this.MediaCapture_Failed;

            // Initialize MediaCapture
            try
            {
                await _mediaCapture.InitializeAsync(settings);
                // Cache the media properties as we'll need them later.
                var deviceController = _mediaCapture.VideoDeviceController;
                VideoEncodingProperties videoProperties = deviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

                _videoWidth = (uint)videoProperties.Width;
                _videoHeight = (uint)videoProperties.Height;

                _isCaptureInitialized = true;
            }
            catch (UnauthorizedAccessException)
            {
                Debug.WriteLine("The app was denied access to the camera");
            }

            // If initialization succeeded, start the preview
            if (_isCaptureInitialized)
            {
                // Figure out where the camera is located
                if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                {
                    // No information on the location of the camera, assume it's an external camera, not integrated on the device
                    _externalCamera = true;
                }
                else
                {
                    // Camera is fixed on the device
                    _externalCamera = false;

                    // Only mirror the preview if the camera is on the front panel
                    _mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                }

                //StartPreviewAsync
                // Prevent the device from sleeping while the preview is running
                _displayRequest.RequestActive();

                // Set the preview source in the UI and mirror it if necessary
                PreviewControl.Source = _mediaCapture;
                PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

                // Start the preview
                await _mediaCapture.StartPreviewAsync();
                _previewProperties = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

                // Initialize the preview to the current orientation
                if (_previewProperties != null)
                {
                    await SetPreviewRotationAsync();
                }

                _faceTracker = await FaceTracker.CreateAsync();
                TimeSpan timerInterval = TimeSpan.FromMilliseconds(200); // 66-15 fps, 200-5 fps
                _frameProcessingTimer = ThreadPoolTimer.CreatePeriodicTimer(new Windows.System.Threading.TimerElapsedHandler(ProcessCurrentVideoFrame), timerInterval);
            }
        }

        /// <summary>
        /// Cleans up the camera resources (after stopping any video recording and/or preview if necessary) and unregisters from MediaCapture events
        /// </summary>
        /// <returns></returns>
        public async Task DeInit()
        {
            RegisterEventHandlers();
            Debug.WriteLine("DeInit-Facedetector");
            UnregisterEventHandlers();

            if (_isCaptureInitialized)
            {
                if (_previewProperties != null)
                {
                    // The call to stop the preview is included here for completeness, but can be
                    // safely removed if a call to MediaCapture.Dispose() is being made later,
                    // as the preview will be automatically stopped at that point
                    await StopPreviewAsync();
                }
                _isCaptureInitialized = false;
            }
            if (_frameProcessingTimer != null)
            {
                _frameProcessingTimer.Cancel();
                _frameProcessingTimer = null;
            }
            if (_mediaCapture != null)
            {
                _mediaCapture.Failed -= MediaCapture_Failed;
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }

            if(_faceMetaData != null)
            {
                _faceMetaData.Close();
            }

            if(_dataSender != null)
            {
                _dataSender.Close();
            }
        }

        private async void Restart()
        {
            await DeInit();
            FaceCollection.Clear();
            lastSendImage.Source = null;
            cameraSelectionList.Visibility = Visibility.Visible;
            await Initialize(_faceKey, _emotionKey);
        }
        /// <summary>
        /// Gets the current orientation of the UI in relation to the device (when AutoRotationPreferences cannot be honored) and applies a corrective rotation to the preview
        /// </summary>
        private async Task SetPreviewRotationAsync()
        {
            // Only need to update the orientation if the camera is mounted on the device
            if (_externalCamera) return;

            // Calculate which way and how far to rotate the preview
            int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored
            if (_mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            if (_mediaCapture != null)
            {
                // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
                var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
                props.Properties.Add(RotationKey, rotationDegrees);
                await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
            }
        }

        /// <summary>
        /// Stops the preview and deactivates a display request, to allow the screen to go into power saving modes
        /// </summary>
        /// <returns></returns>
        private async Task StopPreviewAsync()
        {
            if(_mediaCapture == null)
            {
                return;
            }

            // Stop the preview
            _previewProperties = null;
            await _mediaCapture.StopPreviewAsync();

            // Use the dispatcher because this method is sometimes called from non-UI threads
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Cleanup the UI
                PreviewControl.Source = null;

                // Allow the device screen to sleep now that the preview is stopped
                _displayRequest.RequestRelease();
            });
        }

        private void ProcessCurrentVideoFrame(ThreadPoolTimer timer)
        {
            var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,async () =>
            {    
                if(_mediaCapture == null)
                {
                    return;
                }
                // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
                // In this situation, don't wait on the semaphore but exit immediately.
                if (!_frameProcessingSemaphore.Wait(0))
                {
                    return;
                }

                try
                {
                    IList<DetectedFace> faces = await GetFacesonPreview();

                    if (faces != null && faces.Count > 0)
                    {
                        StartOnLineDetection();
                    }

                    HighlightDetectedFaces(faces);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ProcessCurrentVideoFrame failed: " + ex.Message);
                }
                finally
                {
                    _frameProcessingSemaphore.Release();
                }
            });
        }

        private async Task<IList<DetectedFace>> GetFacesonPreview()
        {
            IList<DetectedFace> faces = null;

            if(_mediaCapture == null)
            {
                return faces;
            }

            if (_mediaCapture.CameraStreamState == Windows.Media.Devices.CameraStreamState.Shutdown)
            {
                Restart();
            }
            else if (_mediaCapture.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming)
            {
                try
                {
                    const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                    using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)_videoWidth, (int)_videoHeight))
                    {
                        await _mediaCapture.GetPreviewFrameAsync(previewFrame);
                        if (previewFrame.SoftwareBitmap != null)
                        {
                            // The returned VideoFrame should be in the supported NV12 format but we need to verify this.
                            if (Windows.Media.FaceAnalysis.FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                            {
                                faces = await this._faceTracker.ProcessNextFrameAsync(previewFrame);
                            }
                        }
                    }
                }catch(Exception ex)
                {
                    Debug.WriteLine("GetFacesonPreview failed : " + ex.Message);
                }
            }

            return faces;
        }

        private async void StartOnLineDetection()
        {
            try
            {
                if (_mediaCapture != null)
                {
                    //For online we need the frame in different format
                    const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Bgra8;
                    using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)_videoWidth, (int)_videoHeight))
                    {
                        await _mediaCapture.GetPreviewFrameAsync(previewFrame);
                        _faceMetaData?.DetectFaces(previewFrame.SoftwareBitmap);
                    }
                }
            }
            catch (Exception ex)
            {
                var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    Debug.WriteLine("StartOnLineDetection failed: " + ex.Message);
                });
            }
        }
        private async void FaceMetaData_DetectedFaces(FaceWithEmotions[] faces)
        {
            if (_dataSender == null)
            {
                _dataSender = new DataSender();
            }

            PrintDebugDataToScreen(faces);

            await _dataSender.SendData(faces);
        }
        private void PrintDebugDataToScreen(FaceWithEmotions[] faces)
        {
            var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                //then show the data values
                if (faces != null)
                {
                    FaceCollection.Clear();

                    foreach (FaceWithEmotions faceEmotion in faces)
                    {
                        if (faceEmotion != null && faceEmotion.Face != null)
                        {
                            if (faceEmotion.Bitmap != null)
                            {
                                lastSendImage.Source = faceEmotion.Bitmap;
                            }
                            Emotion emotion = faceEmotion.Emotion;
                            Face face = faceEmotion.Face;
                            FaceAttributes attr = face.FaceAttributes;
                            if (attr != null)
                            {
                                FaceCollection.Insert(0, FaceDetails.FromFaceAndEmotion(face, emotion));
                            }
                        }
                    }
                }
            });
        }
        private void HighlightDetectedFaces(IList<DetectedFace> faces)
        {
            // Remove any existing rectangles from previous events
            FacesCanvas.Children.Clear();

            if (faces == null || faces.Count < 1)
            {
                return;
            }

            double width = this.ActualWidth;
            double height = this.ActualHeight;
  
            // For each detected face
            for (int i = 0; i < faces.Count; i++)
            {
                // Face coordinate units are preview resolution pixels, which can be a different scale from our display resolution, so a conversion may be necessary
                Rectangle faceBoundingBox = ConvertPreviewToUiRectangle(faces[i].FaceBox);

                // Set bounding box stroke properties
                faceBoundingBox.StrokeThickness = 2;

                // Highlight the first face in the set
                faceBoundingBox.Stroke = (i == 0 ? new SolidColorBrush(Colors.Blue) : new SolidColorBrush(Colors.DeepSkyBlue));

                // Add grid to canvas containing all face UI objects
                FacesCanvas.Children.Add(faceBoundingBox);
            }

            // Update the face detection bounding box canvas orientation
            SetFacesCanvasRotation();
        }

        /// <summary>
        /// Uses the current display orientation to calculate the rotation transformation to apply to the face detection bounding box canvas
        /// and mirrors it if the preview is being mirrored
        /// </summary>
        private void SetFacesCanvasRotation()
        {
            // Calculate how much to rotate the canvas
            int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored, just like in SetPreviewRotationAsync
            if (_mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Apply the rotation
            var transform = new RotateTransform { Angle = rotationDegrees };
            FacesCanvas.RenderTransform = transform;

            var previewArea = GetPreviewStreamRectInControl(_previewProperties as VideoEncodingProperties, PreviewControl);

            // For portrait mode orientations, swap the width and height of the canvas after the rotation, so the control continues to overlap the preview
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                FacesCanvas.Width = previewArea.Height;
                FacesCanvas.Height = previewArea.Width;

                // The position of the canvas also needs to be adjusted, as the size adjustment affects the centering of the control
                Canvas.SetLeft(FacesCanvas, previewArea.X - (previewArea.Height - previewArea.Width) / 2);
                Canvas.SetTop(FacesCanvas, previewArea.Y - (previewArea.Width - previewArea.Height) / 2);
            }
            else
            {
                FacesCanvas.Width = previewArea.Width;
                FacesCanvas.Height = previewArea.Height;

                Canvas.SetLeft(FacesCanvas, previewArea.X);
                Canvas.SetTop(FacesCanvas, previewArea.Y);
            }

            // Also mirror the canvas if the preview is being mirrored
            FacesCanvas.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        #region Face detection helpers

        /// <summary>
        /// Takes face information defined in preview coordinates and returns one in UI coordinates, taking
        /// into account the position and size of the preview control.
        /// </summary>
        /// <param name="faceBoxInPreviewCoordinates">Face coordinates as retried from the FaceBox property of a DetectedFace, in preview coordinates.</param>
        /// <returns>Rectangle in UI (CaptureElement) coordinates, to be used in a Canvas control.</returns>
        private Rectangle ConvertPreviewToUiRectangle(BitmapBounds faceBoxInPreviewCoordinates)
        {
            var result = new Rectangle();
            var previewStream = _previewProperties as VideoEncodingProperties;

            // If there is no available information about the preview, return an empty rectangle, as re-scaling to the screen coordinates will be impossible
            if (previewStream == null) return result;

            // Similarly, if any of the dimensions is zero (which would only happen in an error case) return an empty rectangle
            if (previewStream.Width == 0 || previewStream.Height == 0) return result;

            double streamWidth = previewStream.Width;
            double streamHeight = previewStream.Height;

            // For portrait orientations, the width and height need to be swapped
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamHeight = previewStream.Width;
                streamWidth = previewStream.Height;
            }

            // Get the rectangle that is occupied by the actual video feed
            var previewInUI = GetPreviewStreamRectInControl(previewStream, PreviewControl);

            // Scale the width and height from preview stream coordinates to window coordinates
            result.Width = (faceBoxInPreviewCoordinates.Width / streamWidth) * previewInUI.Width;
            result.Height = (faceBoxInPreviewCoordinates.Height / streamHeight) * previewInUI.Height;

            // Scale the X and Y coordinates from preview stream coordinates to window coordinates
            var x = (faceBoxInPreviewCoordinates.X / streamWidth) * previewInUI.Width;
            var y = (faceBoxInPreviewCoordinates.Y / streamHeight) * previewInUI.Height;
            Canvas.SetLeft(result, x);
            Canvas.SetTop(result, y);

            return result;
        }

        /// <summary>
        /// Calculates the size and location of the rectangle that contains the preview stream within the preview control, when the scaling mode is Uniform
        /// </summary>
        /// <param name="previewResolution">The resolution at which the preview is running</param>
        /// <param name="previewControl">The control that is displaying the preview using Uniform as the scaling mode</param>
        /// <returns></returns>
        public Rect GetPreviewStreamRectInControl(VideoEncodingProperties previewResolution, CaptureElement previewControl)
        {
            var result = new Rect();

            // In case this function is called before everything is initialized correctly, return an empty result
            if (previewControl == null || previewControl.ActualHeight < 1 || previewControl.ActualWidth < 1 ||
                previewResolution == null || previewResolution.Height == 0 || previewResolution.Width == 0)
            {
                return result;
            }

            var streamWidth = previewResolution.Width;
            var streamHeight = previewResolution.Height;

            // For portrait orientations, the width and height need to be swapped
            if (_displayOrientation == DisplayOrientations.Portrait || _displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamWidth = previewResolution.Height;
                streamHeight = previewResolution.Width;
            }

            // Start by assuming the preview display area in the control spans the entire width and height both (this is corrected in the next if for the necessary dimension)
            result.Width = previewControl.ActualWidth;
            result.Height = previewControl.ActualHeight;

            // If UI is "wider" than preview, letterboxing will be on the sides
            if ((previewControl.ActualWidth / previewControl.ActualHeight > streamWidth / (double)streamHeight))
            {
                var scale = previewControl.ActualHeight / streamHeight;
                var scaledWidth = streamWidth * scale;

                result.X = (previewControl.ActualWidth - scaledWidth) / 2.0;
                result.Width = scaledWidth;
            }
            else // Preview stream is "wider" than UI, so letterboxing will be on the top+bottom
            {
                var scale = previewControl.ActualWidth / streamWidth;
                var scaledHeight = streamHeight * scale;

                result.Y = (previewControl.ActualHeight - scaledHeight) / 2.0;
                result.Height = scaledHeight;
            }

            return result;
        }

        #endregion

        #region Event handlers

        /// <summary>
        /// Registers event handlers for hardware buttons and orientation sensors, and performs an initial update of the UI rotation
        /// </summary>
        private void RegisterEventHandlers()
        {
            // If there is an orientation sensor present on the device, register for notifications
            if (_orientationSensor != null)
            {
                _orientationSensor.OrientationChanged += OrientationSensor_OrientationChanged;
            }

            _displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;
        }

        /// <summary>
        /// Unregisters event handlers for hardware buttons and orientation sensors
        /// </summary>
        private void UnregisterEventHandlers()
        {
            if (_orientationSensor != null)
            {
                _orientationSensor.OrientationChanged -= OrientationSensor_OrientationChanged;
            }

            _displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;
        }

        /// <summary>
        /// This event will fire when the page is rotated, when the DisplayInformation.AutoRotationPreferences value set in the SetupUiAsync() method cannot be not honored.
        /// </summary>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event data.</param>
        private async void DisplayInformation_OrientationChanged(DisplayInformation sender, object args)
        {
            _displayOrientation = sender.CurrentOrientation;

            if (_previewProperties != null)
            {
                await SetPreviewRotationAsync();
            }
        }


        /// <summary>
        /// Occurs each time the simple orientation sensor reports a new sensor reading.
        /// </summary>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event data.</param>
        private void OrientationSensor_OrientationChanged(SimpleOrientationSensor sender, SimpleOrientationSensorOrientationChangedEventArgs args)
        {
            if (args.Orientation != SimpleOrientation.Faceup && args.Orientation != SimpleOrientation.Facedown)
            {
                // Only update the current orientation if the device is not parallel to the ground. This allows users to take pictures of documents (FaceUp)
                // or the ceiling (FaceDown) in portrait or landscape, by first holding the device in the desired orientation, and then pointing the camera
                // either up or down, at the desired subject.
                //Note: This assumes that the camera is either facing the same way as the screen, or the opposite way. For devices with cameras mounted
                //      on other panels, this logic should be adjusted.
                _deviceOrientation = args.Orientation;
            }
        }

        private async void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            Debug.WriteLine("MediaCapture_Failed: (0x{0:X}) {1}", errorEventArgs.Code, errorEventArgs.Message);
            await DeInit();
        }

        #endregion Event handlers

        #region Helper functions
       
        /// <summary>
        /// Converts the given orientation of the app on the screen to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the app on the screen</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation)
        {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    return 90;
                case DisplayOrientations.LandscapeFlipped:
                    return 180;
                case DisplayOrientations.PortraitFlipped:
                    return 270;
                case DisplayOrientations.Landscape:
                default:
                    return 0;
            }
        }

        #endregion Helper functions
    }
}
