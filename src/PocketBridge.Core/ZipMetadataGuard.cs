using System.Buffers.Binary;

namespace PocketBridge.Core;

/// <summary>Bounds ZIP metadata before ZipArchive allocates its central-directory collection.</summary>
internal static class ZipMetadataGuard
{
    // ZIP layout reference: https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT
    // The transfer protocol permits exactly one entry; metadata must describe exactly one record.
    public static void ValidateSingleEntry(Stream source)
    {
        const int maxMetadata = 256 * 1024;
        if (source.Length < 22) throw Invalid();
        byte[] tail = new byte[(int)Math.Min(source.Length, 22 + ushort.MaxValue)];
        long tailOffset = source.Length - tail.Length;
        ReadAt(source, tailOffset, tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length)
            {
                end = i;
                break;
            }
        }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0) throw Invalid();
        ulong count = U16(tail, end + 10);
        ulong centralSize = U32(tail, end + 12);
        ulong centralOffset = U32(tail, end + 16);
        long metadataEnd = tailOffset + end;
        if (count == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            if (metadataEnd < 20) throw Invalid();
            byte[] locator = new byte[20];
            ReadAt(source, metadataEnd - 20, locator);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1) throw Invalid();
            ulong zip64Offset = U64(locator, 8);
            if (zip64Offset > (ulong)(metadataEnd - 20) || (ulong)(metadataEnd - 20) - zip64Offset < 56) throw Invalid();
            byte[] zip64 = new byte[56];
            ReadAt(source, (long)zip64Offset, zip64);
            ulong recordSize = U64(zip64, 4);
            if (U32(zip64, 0) != 0x06064b50 || recordSize < 44 || recordSize > maxMetadata ||
                zip64Offset + 12 + recordSize != (ulong)(metadataEnd - 20) ||
                U32(zip64, 16) != 0 || U32(zip64, 20) != 0 || U64(zip64, 24) != 1) throw Invalid();
            count = U64(zip64, 32);
            centralSize = U64(zip64, 40);
            centralOffset = U64(zip64, 48);
            metadataEnd = (long)zip64Offset;
        }
        else if (U16(tail, end + 8) != 1) throw Invalid();

        if (count != 1 || centralSize is < 46 or > maxMetadata || centralOffset > (ulong)metadataEnd ||
            centralSize != (ulong)metadataEnd - centralOffset) throw Invalid();
        byte[] centralHeader = new byte[46];
        ReadAt(source, (long)centralOffset, centralHeader);
        if (U32(centralHeader, 0) != 0x02014b50 || U16(centralHeader, 34) != 0 ||
            centralSize != (ulong)(46 + U16(centralHeader, 28) + U16(centralHeader, 30) + U16(centralHeader, 32))) throw Invalid();
        source.Position = 0;
    }

    private static void ReadAt(Stream source, long offset, byte[] buffer)
    {
        if (offset < 0 || offset > source.Length - buffer.Length) throw Invalid();
        source.Position = offset;
        source.ReadExactly(buffer);
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
    private static InvalidDataException Invalid() => new("압축 파일은 하나의 파일과 제한된 ZIP 메타데이터만 포함해야 합니다.");
}
