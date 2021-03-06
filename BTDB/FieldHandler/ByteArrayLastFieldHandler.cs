using System;
using BTDB.IL;
using BTDB.StreamLayer;

namespace BTDB.FieldHandler
{
    public class ByteArrayLastFieldHandler : SimpleFieldHandlerBase
    {
        public ByteArrayLastFieldHandler()
            : base("Byte[]Last",
                EmitHelpers.GetMethodInfo(() => default(AbstractBufferedReader).ReadByteArrayRawTillEof()),
                null,
                EmitHelpers.GetMethodInfo(() => default(AbstractBufferedWriter).WriteByteArrayRaw(null)))
        {
        }

        public override bool IsCompatibleWith(Type type, FieldHandlerOptions options)
        {
            if (!options.HasFlag(FieldHandlerOptions.AtEndOfStream)) return false;
            return base.IsCompatibleWith(type, options);
        }
    }
}