using System;
using System.Linq;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using MobileCoreServices;
using Photos;
using UIKit;

namespace Xamarin.Essentials
{
    public static partial class MediaPicker
    {
        static UIImagePickerController picker;

        static bool PlatformIsCaptureSupported
            => UIImagePickerController.IsSourceTypeAvailable(UIImagePickerControllerSourceType.Camera);

        static Task<FileResult> PlatformPickPhotoAsync(MediaPickerOptions options)
            => PhotoAsync(options, true, true);

        static Task<FileResult> PlatformCapturePhotoAsync(MediaPickerOptions options)
            => PhotoAsync(options, true, false);

        static Task<FileResult> PlatformPickVideoAsync(MediaPickerOptions options)
            => PhotoAsync(options, false, true);

        static Task<FileResult> PlatformCaptureVideoAsync(MediaPickerOptions options)
            => PhotoAsync(options, false, false);

        static async Task<FileResult> PhotoAsync(MediaPickerOptions options, bool photo, bool pickExisting)
        {
            var sourceType = pickExisting ? UIImagePickerControllerSourceType.PhotoLibrary : UIImagePickerControllerSourceType.Camera;
            var mediaType = photo ? UTType.Image : UTType.Movie;

            if (!UIImagePickerController.IsSourceTypeAvailable(sourceType))
                throw new FeatureNotSupportedException();
            if (!UIImagePickerController.AvailableMediaTypes(sourceType).Contains(mediaType))
                throw new FeatureNotSupportedException();

            // microphone only needed if video will be captured
            if (!photo && !pickExisting)
                await Permissions.EnsureGrantedAsync<Permissions.Microphone>();

            // Check if picking existing or not and ensure permission accordingly as they can be set independently from each other
            if (pickExisting && !Platform.HasOSVersion(11, 0))
                await Permissions.EnsureGrantedAsync<Permissions.Photos>();

            if (!pickExisting)
                await Permissions.EnsureGrantedAsync<Permissions.Camera>();

            var vc = Platform.GetCurrentViewController(true);

            picker = new UIImagePickerController();
            picker.SourceType = sourceType;
            picker.MediaTypes = new string[] { mediaType };
            picker.AllowsEditing = false;
            if (!photo && !pickExisting)
                picker.CameraCaptureMode = UIImagePickerControllerCameraCaptureMode.Video;

            if (!string.IsNullOrWhiteSpace(options?.Title))
                picker.Title = options.Title;

            if (DeviceInfo.Idiom == DeviceIdiom.Tablet && picker.PopoverPresentationController != null && vc.View != null)
                picker.PopoverPresentationController.SourceRect = vc.View.Bounds;

            var tcs = new TaskCompletionSource<FileResult>(picker);
            picker.Delegate = new PhotoPickerDelegate
            {
                CompletedHandler = async info =>
                {
                    GetFileResult(info, tcs);
                    await vc.DismissViewControllerAsync(true);
                }
            };

            if (picker.PresentationController != null)
            {
                picker.PresentationController.Delegate = new Platform.UIPresentationControllerDelegate
                {
                    DismissHandler = () => GetFileResult(null, tcs)
                };
            }

            await vc.PresentViewControllerAsync(picker, true);

            var result = await tcs.Task;

            picker?.Dispose();
            picker = null;

            return result;
        }

        static void GetFileResult(NSDictionary info, TaskCompletionSource<FileResult> tcs)
        {
            try
            {
                tcs.TrySetResult(DictionaryToMediaFile(info));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        static FileResult DictionaryToMediaFile(NSDictionary info)
        {
            if (info == null)
                return null;

            PHAsset phAsset = null;
            NSUrl assetUrl = null;

            if (Platform.HasOSVersion(11, 0))
            {
                assetUrl = info[UIImagePickerController.ImageUrl] as NSUrl;

                // Try the MediaURL sometimes used for videos
                if (assetUrl == null)
                    assetUrl = info[UIImagePickerController.MediaURL] as NSUrl;

                if (assetUrl != null)
                {
                    if (!assetUrl.Scheme.Equals("assets-library", StringComparison.InvariantCultureIgnoreCase))
                        return new UIDocumentFileResult(assetUrl);

                    phAsset = info.ValueForKey(UIImagePickerController.PHAsset) as PHAsset;
                }
            }

            if (phAsset == null)
            {
                assetUrl = info[UIImagePickerController.ReferenceUrl] as NSUrl;

                if (assetUrl != null)
                    phAsset = PHAsset.FetchAssets(new NSUrl[] { assetUrl }, null)?.LastObject as PHAsset;
            }

            if (phAsset == null || assetUrl == null)
            {
                var img = info.ValueForKey(UIImagePickerController.OriginalImage) as UIImage;

                // assume we want to serialise and share the image
                img = MediaUtils.OrientateImageForSerialisation(img);

                if (img != null)
                    return new UIImageFileResult(img);
            }

            if (phAsset == null || assetUrl == null)
                return null;

            string originalFilename;

            if (Platform.HasOSVersion(9, 0))
                originalFilename = PHAssetResource.GetAssetResources(phAsset).FirstOrDefault()?.OriginalFilename;
            else
                originalFilename = phAsset.ValueForKey(new NSString("filename")) as NSString;

            return new PHAssetFileResult(assetUrl, phAsset, originalFilename);
        }

        class PhotoPickerDelegate : UIImagePickerControllerDelegate
        {
            public Action<NSDictionary> CompletedHandler { get; set; }

            public override void FinishedPickingMedia(UIImagePickerController picker, NSDictionary info) =>
                CompletedHandler?.Invoke(info);

            public override void Canceled(UIImagePickerController picker) =>
                CompletedHandler?.Invoke(null);
        }

        class MediaUtils
        {
            /// <summary>
            /// Serialising the image and sharing it with other platforms like web aren't always able to read orientation prior to presentation, rotate the image to match this
            /// </summary>
            /// <param name="image"></param>
            /// <returns></returns>
            public static UIImage OrientateImageForSerialisation(UIImage image)
            {
                CGSize size = image.Size;
                UIGraphics.BeginImageContext(size);
                CGContext context = UIGraphics.GetCurrentContext();
                double radians = 0;
                CGAffineTransform transform = CGAffineTransform.MakeIdentity();
                switch (image.Orientation)
                {
                    case UIImageOrientation.Down:
                    case UIImageOrientation.DownMirrored:
                        radians = Math.PI;
                        context.TranslateCTM(image.Size.Width, image.Size.Height);
                        context.RotateCTM((nfloat)radians);
                        break;
                    case UIImageOrientation.Left:
                    case UIImageOrientation.LeftMirrored:
                        radians = Math.PI / 2.0f;
                        context.TranslateCTM(image.Size.Width, 0);
                        context.RotateCTM((nfloat)radians);
                        break;
                    case UIImageOrientation.Right:
                    case UIImageOrientation.RightMirrored:
                        radians = -Math.PI / 2.0f;
                        context.TranslateCTM(0, image.Size.Height);
                        context.RotateCTM((nfloat)radians);
                        break;
                }

                switch (image.Orientation)
                {
                    case UIImageOrientation.UpMirrored:
                    case UIImageOrientation.DownMirrored:
                        context.TranslateCTM(image.Size.Width, 0);
                        context.ScaleCTM(-1, 1);
                        break;
                    case UIImageOrientation.LeftMirrored:
                    case UIImageOrientation.RightMirrored:
                        context.TranslateCTM(image.Size.Height, 0);
                        context.ScaleCTM(-1, 1);
                        break;
                    case UIImageOrientation.Right:
                    case UIImageOrientation.Left:
                        /* flip vertically */
                        context.ScaleCTM(-1, 1);
                        context.TranslateCTM(-size.Height, 0);
                        break;
                    default:
                        break;
                }

                switch (image.Orientation)
                {
                    case UIImageOrientation.Left:
                    case UIImageOrientation.LeftMirrored:
                    case UIImageOrientation.Right:
                    case UIImageOrientation.RightMirrored:
                        context.DrawImage(new CGRect(0, 0, size.Height, size.Width), image.CGImage);
                        break;
                    default:
                        context.DrawImage(new CGRect(0, 0, size.Width, size.Height), image.CGImage);
                        break;
                }

                UIImage imageCopy = UIGraphics.GetImageFromCurrentImageContext();
                UIGraphics.EndImageContext();

                return imageCopy;
            }
        }
    }
}
