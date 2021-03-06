using System.IO;
using BTDB.Buffer;

namespace BTDB.EventStoreLayer
{
    public class StreamEventFileStorage : IEventFileStorage
    {
        readonly Stream _stream;

        public StreamEventFileStorage(Stream stream)
        {
            _stream = stream;
            MaxBlockSize = 4*1024*1024;
        }

        public uint MaxBlockSize { get; set; }

        public uint Read(ByteBuffer buf, ulong position)
        {
            _stream.Position = (long)position;
            return (uint)_stream.Read(buf.Buffer, buf.Offset, buf.Length);
        }

        public void Write(ByteBuffer buf, ulong position)
        {
            _stream.Position = (long)position;
            _stream.Write(buf.Buffer, buf.Offset, buf.Length);
            _stream.Flush();
        }
    }
}