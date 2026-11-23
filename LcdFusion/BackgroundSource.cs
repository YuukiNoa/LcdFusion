using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LcdFusion
{
    // Common contract for any drawable LCD background media (static image, GIF, external wallpaper renderer).
    internal interface IBackgroundSource : IDisposable
    {
        bool IsAnimated { get; }
        bool HasPendingFrame { get; }
        string StatusError { get; }

        void Advance(int deltaMs);
        Image AcquireFrame();
        void MarkFrameConsumed();
    }

    internal sealed class StaticImageSource : IBackgroundSource
    {
        private Image _image;
        private bool _consumed;

        public StaticImageSource(Image image)
        {
            _image = image;
        }

        public bool IsAnimated { get { return false; } }
        public bool HasPendingFrame { get { return !_consumed && _image != null; } }
        public string StatusError { get { return null; } }

        public void Advance(int deltaMs) { }

        public Image AcquireFrame() { return _image; }

        public void MarkFrameConsumed() { _consumed = true; }

        public void Dispose()
        {
            if (_image != null)
            {
                try { _image.Dispose(); } catch { }
                _image = null;
            }
        }
    }

    // Decodes one GIF and exposes the current frame with per-frame timing.
    internal sealed class GifState : IBackgroundSource
    {
        private readonly Image _image;
        private readonly MemoryStream _stream;
        private readonly int[] _delaysMs;
        private readonly int _count;
        private int _index;
        private int _elapsed;
        private bool _dirty = true;

        private GifState(Image image, MemoryStream stream, int[] delays)
        {
            _image = image; _stream = stream; _delaysMs = delays; _count = delays.Length;
        }

        public bool IsAnimated { get { return _count > 1; } }
        public bool HasPendingFrame { get { return _dirty; } }
        public string StatusError { get { return null; } }

        public static GifState Load(string path)
        {
            return LoadBytes(File.ReadAllBytes(path));
        }

        public static GifState LoadBytes(byte[] data)
        {
            MemoryStream stream = new MemoryStream(data);
            Image image = Image.FromStream(stream);
            int count = 1;
            try { count = image.GetFrameCount(FrameDimension.Time); } catch { count = 1; }
            if (count < 1) count = 1;
            int[] delays = new int[count];
            try
            {
                PropertyItem item = image.GetPropertyItem(0x5100);
                for (int i = 0; i < count; i++)
                {
                    int ticks = BitConverter.ToInt32(item.Value, i * 4) * 10;
                    delays[i] = ticks <= 10 ? 100 : ticks;
                }
            }
            catch { for (int i = 0; i < count; i++) delays[i] = 100; }
            return new GifState(image, stream, delays);
        }

        public Image CurrentFrame()
        {
            _image.SelectActiveFrame(FrameDimension.Time, _index);
            return _image;
        }

        public Image AcquireFrame()
        {
            return CurrentFrame();
        }

        public void MarkFrameConsumed()
        {
            _dirty = false;
        }

        public void Advance(int deltaMs)
        {
            if (_count <= 1) return;
            int prev = _index;
            _elapsed += deltaMs;
            int guard = 0;
            while (_elapsed >= _delaysMs[_index] && guard++ < _count + 1)
            {
                _elapsed -= _delaysMs[_index];
                _index = (_index + 1) % _count;
            }
            if (_index != prev) _dirty = true;
        }

        public void Dispose()
        {
            try { _image.Dispose(); } catch { }
            try { _stream.Dispose(); } catch { }
        }
    }
}
