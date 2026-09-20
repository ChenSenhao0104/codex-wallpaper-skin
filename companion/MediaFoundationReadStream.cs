using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

/// <summary>
/// Managed read-only, seekable <see cref="IMFByteStream"/> over an in-memory
/// buffer.
///
/// It exists so the decode verification never touches the file system. Beyond
/// being faster, that keeps the decodability check runnable in environments
/// where a Media Foundation file source is denied: the encoder's own output is
/// handed straight back to Media Foundation's reader.
/// </summary>
internal sealed class MediaFoundationReadStream : IMFByteStream, IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _data;
    private readonly IntPtr _interfacePointer;
    private int _position;
    private uint _lastAsyncRead;
    private bool _disposed;

    public MediaFoundationReadStream(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _interfacePointer = Marshal.GetComInterfaceForObject(this, typeof(IMFByteStream));
    }

    internal IntPtr InterfacePointer => _interfacePointer;

    public int GetCapabilities(out uint capabilities)
    {
        capabilities = MediaFoundationInterop.ByteStreamIsReadable | MediaFoundationInterop.ByteStreamIsSeekable;
        return MediaFoundationInterop.S_OK;
    }

    public int GetLength(out ulong length)
    {
        length = (ulong)_data.Length;
        return MediaFoundationInterop.S_OK;
    }

    public int SetLength(ulong length) => MediaFoundationInterop.E_NOTIMPL;

    public int GetCurrentPosition(out ulong position)
    {
        lock (_gate)
        {
            position = (ulong)_position;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int SetCurrentPosition(ulong position)
    {
        lock (_gate)
        {
            if (position > (ulong)_data.Length)
            {
                return MediaFoundationInterop.E_INVALIDARG;
            }
            _position = (int)position;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int IsEndOfStream(out int endOfStream)
    {
        lock (_gate)
        {
            endOfStream = _position >= _data.Length ? 1 : 0;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int Read(IntPtr buffer, uint count, out uint read)
    {
        read = 0;
        if (buffer == IntPtr.Zero)
        {
            return MediaFoundationInterop.E_POINTER;
        }
        int available;
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaFoundationInterop.E_UNEXPECTED;
            }
            available = Math.Min((int)Math.Min(count, int.MaxValue), _data.Length - _position);
            if (available <= 0)
            {
                return MediaFoundationInterop.S_OK;
            }
            Marshal.Copy(_data, _position, buffer, available);
            _position += available;
        }
        read = (uint)available;
        return MediaFoundationInterop.S_OK;
    }

    public int BeginRead(IntPtr buffer, uint count, IntPtr callback, IntPtr state)
    {
        var result = Read(buffer, count, out var read);
        if (callback != IntPtr.Zero)
        {
            _lastAsyncRead = read;
            MediaFoundationInterop.CompleteAsyncCallback(callback, state, result);
        }
        return result;
    }

    public int EndRead(IntPtr result, out uint read)
    {
        read = _lastAsyncRead;
        return MediaFoundationInterop.S_OK;
    }

    public int Write(IntPtr buffer, uint count, out uint written)
    {
        written = 0;
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int BeginWrite(IntPtr buffer, uint count, IntPtr callback, IntPtr state)
    {
        if (callback != IntPtr.Zero)
        {
            MediaFoundationInterop.CompleteAsyncCallback(callback, state, MediaFoundationInterop.E_NOTIMPL);
        }
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int EndWrite(IntPtr result, out uint written)
    {
        written = 0;
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int Seek(int seekOrigin, long seekOffset, uint seekFlags, out ulong position)
    {
        lock (_gate)
        {
            var target = seekOrigin == MediaFoundationInterop.SeekOriginBegin
                ? seekOffset
                : _position + seekOffset;
            if (target < 0 || target > _data.Length)
            {
                position = (ulong)Math.Clamp(target, 0, _data.Length);
                return MediaFoundationInterop.E_INVALIDARG;
            }
            _position = (int)target;
            position = (ulong)_position;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int Flush() => MediaFoundationInterop.S_OK;

    public int Close()
    {
        Dispose();
        return MediaFoundationInterop.S_OK;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        if (_interfacePointer != IntPtr.Zero)
        {
            Marshal.Release(_interfacePointer);
        }
    }
}
