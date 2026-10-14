using Together;
using LiteNetLib.Utils;
using System.Collections.Generic;

namespace CatPatchspace
{
    internal static class CatPatchNetId
    {
        public static readonly knetid None = new knetid(ushort.MaxValue);

        public static bool IsNone(knetid id)
        {
            return id.id == ushort.MaxValue;
        }

        public static bool Same(knetid a, knetid b)
        {
            return a.id == b.id;
        }

        public static ulong ToUlong(knetid id)
        {
            return id.id;
        }

        public static string ToDisplay(knetid id)
        {
            return id.id.ToString();
        }

        public static void Write(NetDataWriter writer, knetid id)
        {
            writer.Put(id.id);
        }

        public static knetid Read(NetDataReader reader)
        {
            return new knetid(reader.GetUShort());
        }

        public static HashSet<ulong> ToUlongSet(IEnumerable<knetid> ids)
        {
            HashSet<ulong> values = new HashSet<ulong>();
            if (ids == null)
                return values;

            foreach (knetid id in ids)
                values.Add(ToUlong(id));

            return values;
        }
    }
}
