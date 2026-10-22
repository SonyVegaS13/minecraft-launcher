using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace Solaris.Id.Api;

/// <summary>Bounded PNG validation including chunk CRCs and the full scanline stream.
/// Accepts the noninterlaced 8-bit RGB/RGBA PNG used by Minecraft skins.</summary>
public static class SkinPng
{
    public static bool IsValid(byte[] data)
    {
        if (data.Length is < 57 or > 524288 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) return false;
        int offset = 8, channels = 0; bool header = false, ended = false, image = false, idatEnded = false;
        using var compressed = new MemoryStream();
        while (offset <= data.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (length > 524288 || (long)offset + 12 + length > data.Length) return false;
            int size = (int)length;
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            if (!type.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) return false;
            if (Crc(data.AsSpan(offset + 4, size + 4)) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8 + size, 4))) return false;
            var body = data.AsSpan(offset + 8, size);
            if (!header)
            {
                if (type != "IHDR" || size != 13 || BinaryPrimitives.ReadUInt32BigEndian(body[..4]) != 64 || BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4,4)) != 64 || body[8] != 8 || body[9] is not (2 or 6) || body[10] != 0 || body[11] != 0 || body[12] != 0) return false;
                channels = body[9] == 6 ? 4 : 3; header = true;
            }
            else if (type == "IHDR" || type is "acTL" or "fcTL" or "fdAT") return false;
            else if (type == "IDAT")
            {
                if (idatEnded) return false;
                image = true; compressed.Write(body);
            }
            else if (type == "IEND")
            {
                if (size != 0 || !image) return false;
                ended = true; offset += 12; break;
            }
            else
            {
                if (image) idatEnded = true;
                if (char.IsUpper(type[0]) && type != "PLTE") return false;
                if (type == "PLTE" && (image || size == 0 || size > 768 || size % 3 != 0)) return false;
            }
            offset += size + 12;
        }
        if (!header || !ended || offset != data.Length) return false;
        try
        {
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            int stride = channels * 64;
            for (int row = 0; row < 64; row++)
            {
                int filter = zlib.ReadByte(); if (filter is < 0 or > 4) return false;
                for (int pixelByte = 0; pixelByte < stride; pixelByte++) if (zlib.ReadByte() < 0) return false;
            }
            return zlib.ReadByte() == -1;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
