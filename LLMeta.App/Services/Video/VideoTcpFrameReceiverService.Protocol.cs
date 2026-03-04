using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;

namespace LLMeta.App.Services;

public sealed partial class VideoTcpFrameReceiverService
{
    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken
            );
            if (read == 0)
            {
                throw new EndOfStreamException("Video stream ended while reading.");
            }

            offset += read;
        }
    }

    private static VideoHeader ParseHeader(byte[] headerBuffer)
    {
        var span = headerBuffer.AsSpan();
        return new VideoHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4)),
            span[4],
            span[5],
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(6, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(10, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(18, 4))
        );
    }

    private static void ValidatePayloadAgainstFlags(byte[] payload, byte flags)
    {
        var (hasSps, hasPps, hasIdr) = ParseAnnexBNalKinds(payload);
        var hasCodecConfigFlag = (flags & CodecConfigFlagMask) != 0;
        var isKeyFrameFlag = (flags & KeyFrameFlagMask) != 0;
        var hasCodecConfigPayload = hasSps && hasPps;

        if (hasCodecConfigFlag != hasCodecConfigPayload)
        {
            throw new InvalidDataException(
                $"Video hasCodecConfig mismatch. flag={hasCodecConfigFlag} payload={hasCodecConfigPayload}"
            );
        }

        if (isKeyFrameFlag != hasIdr)
        {
            throw new InvalidDataException(
                $"Video isKeyFrame mismatch. flag={isKeyFrameFlag} payload={hasIdr}"
            );
        }
    }

    private static (bool HasSps, bool HasPps, bool HasIdr) ParseAnnexBNalKinds(byte[] payload)
    {
        if (payload.Length < 5)
        {
            throw new InvalidDataException("Video payload is too short for Annex-B.");
        }

        if (!IsStartCodeAt(payload, 0))
        {
            throw new InvalidDataException(
                "Video payload must start with Annex-B start code 00 00 00 01."
            );
        }

        var hasSps = false;
        var hasPps = false;
        var hasIdr = false;
        var nalCount = 0;
        var offset = 0;

        while (offset < payload.Length)
        {
            if (!IsStartCodeAt(payload, offset))
            {
                throw new InvalidDataException(
                    $"Video payload has invalid Annex-B boundary at offset {offset}."
                );
            }

            var nalStart = offset + 4;
            var nextStartCode = FindNextStartCode(payload, nalStart);
            var nalEnd = nextStartCode >= 0 ? nextStartCode : payload.Length;
            if (nalStart >= nalEnd)
            {
                throw new InvalidDataException("Video payload contains an empty NAL unit.");
            }

            var nalType = payload[nalStart] & 0x1F;
            if (nalType == 7)
            {
                hasSps = true;
            }
            else if (nalType == 8)
            {
                hasPps = true;
            }
            else if (nalType == 5)
            {
                hasIdr = true;
            }

            nalCount++;
            if (nextStartCode < 0)
            {
                break;
            }

            offset = nextStartCode;
        }

        if (nalCount == 0)
        {
            throw new InvalidDataException("Video payload does not contain a valid NAL unit.");
        }

        return (hasSps, hasPps, hasIdr);
    }

    private static bool IsStartCodeAt(byte[] payload, int offset)
    {
        return offset + 4 <= payload.Length
            && payload[offset] == 0
            && payload[offset + 1] == 0
            && payload[offset + 2] == 0
            && payload[offset + 3] == 1;
    }

    private static int FindNextStartCode(byte[] payload, int searchStart)
    {
        for (var i = searchStart; i <= payload.Length - 4; i++)
        {
            if (IsStartCodeAt(payload, i))
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct VideoHeader(
        uint Magic,
        byte Version,
        byte Flags,
        uint Sequence,
        ulong TimestampUnixMs,
        uint PayloadLength
    );
}
