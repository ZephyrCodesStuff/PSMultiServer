using PSMultiServer.Addons.Medius.RT.Common;
using PSMultiServer.Addons.Medius.Server.Common.Stream;

namespace PSMultiServer.Addons.Medius.RT.Models
{
    [ScertMessage(RT_MSG_TYPE.RT_MSG_SERVER_TIMEBASE_QUERY_NOTIFY)]
    public class RT_MSG_SERVER_TIMEBASE_QUERY_NOTIFY : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_SERVER_TIMEBASE_QUERY_NOTIFY;

        public uint ClientTime { get; set; }
        public uint ServerTime { get; set; }

        public override void Deserialize(MessageReader reader)
        {
            ClientTime = reader.ReadUInt32();
            ServerTime = reader.ReadUInt32();
        }

        public override void Serialize(MessageWriter writer)
        {
            writer.Write(ClientTime);
            writer.Write(ServerTime);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"ClientTime: {ClientTime} " +
                $"ServerTime: {ServerTime}";
        }
    }
}