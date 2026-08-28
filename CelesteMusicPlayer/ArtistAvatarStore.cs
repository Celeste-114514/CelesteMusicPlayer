using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 艺术家头像本地存储（按艺术家名哈希文件名，保存在应用本地目录）。
    /// </summary>
    public static class ArtistAvatarStore
    {
        private static string GetFolderPath()
        {
            string root;
            try
            {
                root = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            string folder = Path.Combine(root, "ArtistAvatars");
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string GetAvatarFilePath(string artistName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
            string name = Convert.ToHexString(hash.AsSpan(0, 8));
            return Path.Combine(GetFolderPath(), name + ".png");
        }

        public static async Task<BitmapImage?> TryLoadAsync(string artistName)
        {
            string path = GetAvatarFilePath(artistName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream stream = await file.OpenReadAsync();
                await image.SetSourceAsync(stream);
                return image;
            }
            catch
            {
                return null;
            }
        }

        public static async Task SavePngAsync(string artistName, byte[] bgraPixels, uint width, uint height)
        {
            string path = GetAvatarFilePath(artistName);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(GetFolderPath());
            StorageFile file = await folder.CreateFileAsync(
                Path.GetFileName(path),
                CreationCollisionOption.ReplaceExisting);

            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                width,
                height,
                96,
                96,
                bgraPixels);
            await encoder.FlushAsync();
        }

        public static bool HasCustomAvatar(string artistName)
            => File.Exists(GetAvatarFilePath(artistName));

        public static void DeleteCustomAvatar(string artistName)
        {
            string path = GetAvatarFilePath(artistName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // ---- 网络头像磁盘缓存（与自定义头像分离，便于下次打开不重新联网搜索）----
        private static string GetWebCacheFolderPath()
        {
            string root;
            try
            {
                root = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            string folder = Path.Combine(root, "ArtistAvatars", "Web");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetWebCacheFilePath(string artistName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
            string name = Convert.ToHexString(hash.AsSpan(0, 8));
            return Path.Combine(GetWebCacheFolderPath(), name + ".jpg");
        }

        public static async Task<BitmapImage?> TryLoadWebAsync(string artistName)
        {
            string path = GetWebCacheFilePath(artistName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream stream = await file.OpenReadAsync();
                await image.SetSourceAsync(stream);
                return image;
            }
            catch
            {
                return null;
            }
        }

        public static async Task SaveWebAsync(string artistName, byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return;
            }

            try
            {
                string path = GetWebCacheFilePath(artistName);
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(GetWebCacheFolderPath());
                StorageFile file = await folder.CreateFileAsync(
                    Path.GetFileName(path),
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, imageBytes);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ArtistAvatarStore.cs", caught); }
        }
    }
}
