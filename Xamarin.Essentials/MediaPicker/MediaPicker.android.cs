using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Provider;
using Java.Util.Zip;
using AndroidUri = Android.Net.Uri;
using Path = System.IO.Path;

namespace Xamarin.Essentials
{
    public static partial class MediaPicker
    {
        static bool PlatformIsCaptureSupported
            => Platform.AppContext.PackageManager.HasSystemFeature(PackageManager.FeatureCameraAny);

        static Task<FileResult> PlatformPickPhotoAsync(MediaPickerOptions options)
            => PlatformPickAsync(options, true);

        static Task<FileResult> PlatformPickVideoAsync(MediaPickerOptions options)
            => PlatformPickAsync(options, false);

        static async Task<FileResult> PlatformPickAsync(MediaPickerOptions options, bool photo)
        {
            var intent = new Intent(Intent.ActionGetContent);
            intent.SetType(photo ? FileSystem.MimeTypes.ImageAll : FileSystem.MimeTypes.VideoAll);

            var pickerIntent = Intent.CreateChooser(intent, options?.Title);

            try
            {
                string path = null;
                void OnResult(Intent intent)
                {
                    // The uri returned is only temporary and only lives as long as the Activity that requested it,
                    // so this means that it will always be cleaned up by the time we need it because we are using
                    // an intermediate activity.

                    path = FileSystem.EnsurePhysicalPath(intent.Data);
                }

                await IntermediateActivity.StartAsync(pickerIntent, Platform.requestCodeMediaPicker, onResult: OnResult);

                // Ensure the file is rotated to the upright orientation
                string filePath = await MediaUtils.OrientateImageForSerialisation(path);

                // Return the file that we just captured
                return new FileResult(filePath);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        static Task<FileResult> PlatformCapturePhotoAsync(MediaPickerOptions options)
            => PlatformCaptureAsync(options, true);

        static Task<FileResult> PlatformCaptureVideoAsync(MediaPickerOptions options)
            => PlatformCaptureAsync(options, false);

        static async Task<FileResult> PlatformCaptureAsync(MediaPickerOptions options, bool photo)
        {
            await Permissions.EnsureGrantedAsync<Permissions.Camera>();

            // StorageWrite no longer exists starting from Android API 33
            if (!Platform.HasApiLevel(33))
                await Permissions.EnsureGrantedAsync<Permissions.StorageWrite>();

            var capturePhotoIntent = new Intent(photo ? MediaStore.ActionImageCapture : MediaStore.ActionVideoCapture);

            if (!Platform.IsIntentSupported(capturePhotoIntent))
                throw new FeatureNotSupportedException($"Either there was no camera on the device or '{capturePhotoIntent.Action}' was not added to the <queries> element in the app's manifest file. See more: https://developer.android.com/about/versions/11/privacy/package-visibility");

            capturePhotoIntent.AddFlags(ActivityFlags.GrantReadUriPermission);
            capturePhotoIntent.AddFlags(ActivityFlags.GrantWriteUriPermission);

            try
            {
                var activity = Platform.GetCurrentActivity(true);

                // Create the temporary file
                var ext = photo
                    ? FileSystem.Extensions.Jpg
                    : FileSystem.Extensions.Mp4;
                var fileName = Guid.NewGuid().ToString("N") + ext;
                var tmpFile = FileSystem.GetEssentialsTemporaryFile(Platform.AppContext.CacheDir, fileName);

                // Set up the content:// uri
                AndroidUri outputUri = null;
                void OnCreate(Intent intent)
                {
                    // Android requires that using a file provider to get a content:// uri for a file to be called
                    // from within the context of the actual activity which may share that uri with another intent
                    // it launches.

                    outputUri ??= FileProvider.GetUriForFile(tmpFile);

                    intent.PutExtra(MediaStore.ExtraOutput, outputUri);
                }

                // Start the capture process
                await IntermediateActivity.StartAsync(capturePhotoIntent, Platform.requestCodeMediaCapture, OnCreate);

                // Ensure the file is rotated to the upright orientation
                string filePath = await MediaUtils.OrientateImageForSerialisation(tmpFile.AbsolutePath);

                // Return the file that we just captured
                return new FileResult(filePath);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        class MediaUtils
        {
            /// <summary>
            /// Serialising the image and sharing it with other platforms like web aren't always able to read orientation prior to presentation, rotate the image to match this
            /// </summary>
            /// <param name="image"></param>
            /// <returns></returns>
            public static async Task<string> OrientateImageForSerialisation(string path)
            {
                var stream = System.IO.File.OpenRead(path);

                byte[] image = new byte[stream.Length];
                stream.Read(image, 0, image.Length);

                // normalise orientation of images based on exif if present
                ExifInterface exif = new ExifInterface(System.IO.File.OpenRead(path));
                System.Console.WriteLine($"{nameof(MediaUtils)}::exif: {exif.ToString()}");
                if (exif != null)
                {
                    int orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 0);
                    System.Console.WriteLine($"{nameof(MediaUtils)}::imgOrient: {orientation.ToString()}");
                    switch (orientation)
                    {
                        case 0: // Undefined
                            break;
                        case 2: // Flip Horizontal
                            break;
                        case 4: // Flip Vertical
                            break;
                        case 1: // Orientation normal
                            break;
                        case 3: // rotate 180
                            image = RotateImage(image, 180);
                            break;
                        case 8: // rotate 270
                            image = RotateImage(image, 270);
                            break;
                        case 6: // rotate 90
                            image = RotateImage(image, 90);
                            break;
                        case 5: // transpose
                            break;
                        case 7: // transverse
                            break;
                    }
                }
                Bitmap resultBitmap = BitmapFactory.DecodeByteArray(image, 0, image.Length);

                // serialise
                string newFilename = $"{Path.GetFileNameWithoutExtension(path)}.rotated.jpg";
                string newPath = Path.Combine(Path.GetDirectoryName(path), newFilename);
                FileStream outStream = new FileStream(newPath, FileMode.Create);

                await resultBitmap.CompressAsync(Bitmap.CompressFormat.Jpeg, 100, outStream);

                return newPath;
            }

            public static byte[] RotateImage(byte[] imageData, int rotation = 0)
            {
                System.Console.WriteLine($"{nameof(MediaPicker)}::RotateImage by {rotation}");

                // loads just the dimensions of the file instead of the entire image
                var options = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeByteArray(imageData, 0, imageData.Length, options);

                int outHeight = options.OutHeight;
                int outWidth = options.OutWidth;

                options.InJustDecodeBounds = false;

                // decode image
                var bitmap = BitmapFactory.DecodeByteArray(imageData, 0, imageData.Length, options);
                var matrix = new Matrix();

                matrix.PreRotate(rotation);

                bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                matrix.Dispose();
                matrix = null;

                byte[] newImageData;
                int quality = 100;

                using (var stream = new System.IO.MemoryStream())
                {
                    bitmap.Compress(Bitmap.CompressFormat.Jpeg, quality, stream);
                    bitmap.Recycle();
                    bitmap.Dispose();
                    bitmap = null;
                    newImageData = stream.ToArray();
                }

                return newImageData;
            }
        }
    }
}
