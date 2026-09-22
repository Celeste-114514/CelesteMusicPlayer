using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 整文件非托管内存只读流：打开时一次性把文件读进 <see cref="Marshal.AllocHGlobal"/> 缓冲区，
    /// 之后所有 Read/Seek 都是纯内存操作——render 实时线程不碰磁盘，
    /// 且字节不走 GC 堆（无 LOH 分配/回收压力）。
    ///
    /// 2026-09-22 C2：取代 <see cref="HiFiOutputBackend.OpenWaveSource"/> 里的
    /// File.ReadAllBytes + MemoryStream 组合——旧做法整首 PCM 落在 managed byte[]，
    /// ≤192MB 的曲目全在大对象堆，叠加无缝预载的「下一首」，两首共 ~384MB LOH，
    /// gen2/LOH 回收 STW 冻渲染线程 = PCM 卡顿根因（与 DSD 侧 BuiltInDsdStream/
    /// DoPWaveSource 整读同一病根，2026-09-22 DSD 内存实锤）。
    /// 改为非托管后：内存仍一次性驻留（磁盘 I/O 在播放前完成，render 线程只读内存），
    /// 但 GC 完全看不见这些字节，GC 卡顿与内存暴涨一并消失。
    ///
    /// 与 MemoryStream 的行为对齐（WaveFileReader 依赖）：
    /// Length/Position/Seek/顺序读语义一致；Position 允许超过 Length（其后 Read 返回 0）。
    /// Dispose 释放非托管缓冲；带终结器兜底（万一 WaveFileReader 未 dispose 也不永久泄漏）。
    /// </summary>
    internal sealed class UnmanagedMemoryFileStream : Stream
    {
        private IntPtr _ptr;
        private readonly long _len;
        private long _pos;
        private bool _disposed;

        private UnmanagedMemoryFileStream(IntPtr ptr, long len)
        {
            _ptr = ptr;
            _len = len;
        }

        /// <summary>整个读入非托管内存并返回只读流。读取失败/文件被删短时抛异常（与旧 ReadAllBytes 行为一致）。</summary>
        public static UnmanagedMemoryFileStream Open(string path)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception caught)
            {
                throw new IOException("无法读取文件长度: " + path, caught);
            }

            if (size <= 0)
            {
                throw new IOException("文件为空或不可读: " + path);
            }

            IntPtr ptr = Marshal.AllocHGlobal((IntPtr)size);
            try
            {
                // 读盘用 ArrayPool 租 1MB 暂存（真池化，不新增 LOH 分配），再 Marshal.Copy 进非托管缓冲
                byte[] scratch = ArrayPool<byte>.Shared.Rent(1 << 20);
                try
                {
                    using var fs = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    long got = 0;
                    while (got < size)
                    {
                        int want = (int)Math.Min(scratch.Length, size - got);
                        int n = fs.Read(scratch, 0, want);
                        if (n <= 0)
                        {
                            break;
                        }

                        Marshal.Copy(scratch, 0, new IntPtr(ptr.ToInt64() + got), n);
                        got += n;
                    }

                    if (got != size)
                    {
                        // 缓存清理可能在开流后删短文件——与旧 ReadAllBytes 的失败语义一致：抛
                        throw new IOException($"文件读取不完整: {path}（{got}/{size}B）");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
            }
            catch
            {
                Marshal.FreeHGlobal(ptr);
                throw;
            }

            return new UnmanagedMemoryFileStream(ptr, size);
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => !_disposed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _len;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _pos;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _pos = value;
            }
        }

        public override void Flush()
        {
            // 只读流，无操作
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            long remain = _len - _pos;
            if (remain <= 0 || count <= 0)
            {
                return 0;
            }

            int take = (int)Math.Min(count, remain);
            Marshal.Copy(new IntPtr(_ptr.ToInt64() + _pos), buffer, offset, take);
            _pos += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _len + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            // 与 MemoryStream 一致：允许seek 到超过 Length 的位置（其后 Read 返回 0）
            _pos = target;
            return _pos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ptr);
                    _ptr = IntPtr.Zero;
                }

                GC.SuppressFinalize(this);
            }

            base.Dispose(disposing);
        }

        ~UnmanagedMemoryFileStream()
        {
            // 兜底：万一上游 WaveFileReader 未 dispose，也不永久泄漏非托管内存
            if (!_disposed && _ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_ptr);
                _ptr = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UnmanagedMemoryFileStream));
            }
        }
    }
}
