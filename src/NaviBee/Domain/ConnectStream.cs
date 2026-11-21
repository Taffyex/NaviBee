using System;
using System.IO;
using System.Net;

namespace MusicBeePlugin
{
    public sealed class ConnectStream : Stream
    {
        private readonly Uri _uri;
        private long _estimatedLength;
        private HttpWebResponse _webResponse;
        private Stream _responseStream;
        private long _position;
        private bool _atEof;
        private bool _disposed;

        private FileStream _teeStream;
        private string _teeTempPath;
        private readonly Action<string> _onTeeCompleted;
        private bool _teeAborted;

        public string ContentType { get; private set; }

        public ConnectStream(Uri uri, long fallbackLength = 0, string teeTempPath = null, Action<string> onTeeCompleted = null)
        {
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _estimatedLength = Math.Max(0, fallbackLength);
            _teeTempPath = teeTempPath;
            _onTeeCompleted = onTeeCompleted;

            Connect(0);
            if (_estimatedLength <= 0 && _webResponse != null && _webResponse.ContentLength > 0)
            {
                _estimatedLength = _webResponse.ContentLength;
            }

            if (!string.IsNullOrEmpty(_teeTempPath) && _onTeeCompleted != null)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_teeTempPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    _teeStream = new FileStream(_teeTempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                }
                catch
                {
                    AbortTee();
                }
            }
        }

        private void Connect(long fromOffset)
        {
            try
            {
                if (_responseStream != null) { try { _responseStream.Dispose(); } catch { } _responseStream = null; }
                if (_webResponse != null) { try { _webResponse.Dispose(); } catch { } _webResponse = null; }

                var httpRequest = (HttpWebRequest)WebRequest.Create(_uri);
                httpRequest.Accept = "*/*";
                httpRequest.Method = "GET";
                httpRequest.Timeout = 30000;
                httpRequest.ReadWriteTimeout = 30000;
                httpRequest.KeepAlive = true;
                SubsonicClient.ConfigureRequest(httpRequest);

                if (fromOffset > 0)
                {
                    AbortTee();
                    httpRequest.AddRange(fromOffset);
                }

                _webResponse = (HttpWebResponse)httpRequest.GetResponse();
                ContentType = _webResponse.ContentType;
                if (SubsonicClient.IsNonAudioErrorContentType(ContentType))
                    throw new IOException($"Server returned non-audio response ({ContentType}).");

                _responseStream = _webResponse.GetResponseStream();
                if (_responseStream == null)
                    throw new IOException("Server returned an empty response stream.");

                if (fromOffset > 0 && _webResponse.StatusCode != HttpStatusCode.PartialContent)
                {
                    if (fromOffset <= 512 * 1024)
                    {
                        SkipBytes(_responseStream, fromOffset);
                    }
                    else
                    {
                        throw new IOException($"Server does not support HTTP Range seeking (status {(int)_webResponse.StatusCode}).");
                    }
                }

                _position = fromOffset;
                _atEof = false;
            }
            catch (Exception ex)
            {
                AbortTee();
                Close();
                throw new IOException($"Failed to connect to media stream at {_uri.GetLeftPart(UriPartial.Path)} (offset {fromOffset}): {ex.Message}", ex);
            }
        }

        private static void SkipBytes(Stream stream, long count)
        {
            byte[] buf = new byte[Math.Min((int)count, 16384)];
            long remaining = count;
            while (remaining > 0)
            {
                int r = stream.Read(buf, 0, (int)Math.Min(remaining, buf.Length));
                if (r <= 0) break;
                remaining -= r;
            }
        }

        private void AbortTee()
        {
            _teeAborted = true;
            if (_teeStream != null)
            {
                try { _teeStream.Dispose(); } catch { }
                _teeStream = null;
            }
            if (!string.IsNullOrEmpty(_teeTempPath))
            {
                try { if (File.Exists(_teeTempPath)) File.Delete(_teeTempPath); } catch { }
                _teeTempPath = null;
            }
        }

        private void CompleteTeeIfReady()
        {
            if (_teeAborted || _teeStream == null || string.IsNullOrEmpty(_teeTempPath))
                return;

            string completedPath = _teeTempPath;
            _teeTempPath = null;
            try
            {
                _teeStream.Flush();
                _teeStream.Dispose();
                _teeStream = null;
                _onTeeCompleted?.Invoke(completedPath);
            }
            catch
            {
                try { if (File.Exists(completedPath)) File.Delete(completedPath); } catch { }
            }
        }

        public override bool CanRead => !_disposed && _responseStream != null && _responseStream.CanRead;
        public override bool CanSeek => !_disposed && _estimatedLength > 0;
        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ConnectStream));
                return _estimatedLength;
            }
        }

        public override long Position
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ConnectStream));
                return _position;
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectStream));
            if (count <= 0 || _atEof) return 0;

            int read;
            try
            {
                read = _responseStream.Read(buffer, offset, count);
            }
            catch (Exception)
            {
                AbortTee();
                if (_estimatedLength > 0 && _position < _estimatedLength)
                {
                    Connect(_position);
                    read = _responseStream.Read(buffer, offset, count);
                }
                else
                {
                    throw;
                }
            }

            if (read > 0)
            {
                _position += read;
                if (!_teeAborted && _teeStream != null)
                {
                    try { _teeStream.Write(buffer, offset, read); }
                    catch { AbortTee(); }
                }
            }
            else if (read == 0)
            {
                _atEof = true;
                CompleteTeeIfReady();
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectStream));
            if (!CanSeek) throw new NotSupportedException("Seeking requires known stream length.");

            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = _position + offset; break;
                case SeekOrigin.End: target = _estimatedLength + offset; break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (target < 0) target = 0;
            if (target > _estimatedLength) target = _estimatedLength;

            if (target == _position)
                return _position;

            if (_estimatedLength > 0 && target >= _estimatedLength)
            {
                AbortTee();
                _position = _estimatedLength;
                _atEof = true;
                return _position;
            }

            _atEof = false;
            long diff = target - _position;
            if (diff > 0 && diff <= 32 * 1024 && _responseStream != null)
            {
                byte[] discard = new byte[Math.Min((int)diff, 4096)];
                while (diff > 0)
                {
                    int toRead = (int)Math.Min(diff, discard.Length);
                    int r = _responseStream.Read(discard, 0, toRead);
                    if (r <= 0) break;
                    if (!_teeAborted && _teeStream != null)
                    {
                        try { _teeStream.Write(discard, 0, r); }
                        catch { AbortTee(); }
                    }
                    _position += r;
                    diff -= r;
                }
                return _position;
            }

            AbortTee();
            Connect(target);
            return _position;
        }

        public override void Flush()
        {
            if (!_disposed && _responseStream != null)
                _responseStream.Flush();
        }

        public override void SetLength(long value) => throw new NotSupportedException("Setting length is not supported on media streams.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Writing is not supported on media streams.");

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    AbortTee();
                    try { _responseStream?.Dispose(); } catch { }
                    try { _webResponse?.Dispose(); } catch { }
                }
            }
            base.Dispose(disposing);
        }
    }
}
