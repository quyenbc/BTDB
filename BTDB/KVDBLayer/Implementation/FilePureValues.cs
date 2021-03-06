using BTDB.StreamLayer;

namespace BTDB.KVDBLayer
{
    internal class FilePureValues : IFileInfo
    {
        readonly long _generation;

        public FilePureValues(AbstractBufferedReader reader)
        {
            _generation = reader.ReadVInt64();
        }

        public FilePureValues(long generation)
        {
            _generation = generation;
        }

        public KVFileType FileType
        {
            get { return KVFileType.PureValues; }
        }

        public long Generation
        {
            get { return _generation; }
        }

        public long SubDBId
        {
            get { return 0; }
        }

        public void WriteHeader(AbstractBufferedWriter writer)
        {
            writer.WriteByteArrayRaw(FileCollectionWithFileInfos.MagicStartOfFile);
            writer.WriteUInt8((byte)KVFileType.PureValues);
            writer.WriteVInt64(_generation);
        }
    }
}