using PSMultiServer.Addons.Medius.RT.Common;
using PSMultiServer.Addons.Medius.Server.Common;
using PSMultiServer.Addons.Medius.Server.Common.Stream;

namespace PSMultiServer.Addons.Medius.RT.Models
{
    [ScertMessage(RT_MSG_TYPE.RT_MSG_SERVER_ECHO)]
    public class RT_MSG_SERVER_ECHO : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_SERVER_ECHO;

        // 
        public uint UnixTimestamp = Utils.GetUnixTime();
        public uint UNK_04 = 0x00000000;

        public override void Deserialize(MessageReader reader)
        {
            UnixTimestamp = reader.ReadUInt32();
            UNK_04 = reader.ReadUInt32();
        }

        public override void Serialize(MessageWriter writer)
        {
            writer.Write(UnixTimestamp);
            writer.Write(UNK_04);
        }
    }
}