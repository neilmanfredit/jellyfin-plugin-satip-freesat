using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SatIpFreesat.DvbSi;

/// <summary>
/// MPEG-TS demuxer and SI section assembler.
/// Feeds 188-byte transport stream packets in via <see cref="FeedPacket"/>;
/// completed SI sections are delivered via <see cref="SectionReady"/>.
/// </summary>
public sealed class TsReader
{
    public const int PacketSize = 188;
    public const byte SyncByte = 0x47;

    // table_id values we care about (subscribe via AddPid)
    private readonly HashSet<int> _subscribedPids = [];

    // Per-PID section assembly state
    private readonly Dictionary<int, SectionState> _sectionState = [];

    public event Action<int, byte[]>? SectionReady; // (pid, section_bytes)

    public void SubscribePid(int pid) => _subscribedPids.Add(pid);

    /// <summary>
    /// Feed a raw MPEG-TS payload (may be multiple packets or a partial RTP payload).
    /// Caller must pass aligned 188-byte packets; partial trailing data is dropped.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset + PacketSize <= data.Length)
        {
            if (data[offset] == SyncByte)
            {
                ProcessPacket(data.Slice(offset, PacketSize));
                offset += PacketSize;
            }
            else
            {
                // Re-sync
                offset++;
            }
        }
    }

    private void ProcessPacket(ReadOnlySpan<byte> pkt)
    {
        bool tei = (pkt[1] & 0x80) != 0; // transport error indicator
        if (tei) return;

        bool pusi = (pkt[1] & 0x40) != 0; // payload unit start indicator
        int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];

        if (!_subscribedPids.Contains(pid)) return;

        int adaptationControl = (pkt[3] >> 4) & 0x03;
        // 0=reserved, 1=payload only, 2=adaptation only, 3=adaptation+payload
        if (adaptationControl == 0 || adaptationControl == 2) return;

        int payloadStart = 4;
        if (adaptationControl == 3)
        {
            int adaptLen = pkt[4];
            payloadStart = 5 + adaptLen;
        }
        if (payloadStart >= PacketSize) return;

        var payload = pkt[payloadStart..];
        if (!_sectionState.TryGetValue(pid, out var state))
        {
            state = new SectionState();
            _sectionState[pid] = state;
        }

        if (pusi)
        {
            int pointer = payload[0];
            payload = payload[1..];

            // Finish any in-progress section (the bytes before pointer_field are its tail)
            if (pointer > 0 && state.Accumulating)
            {
                state.Append(payload[..Math.Min(pointer, payload.Length)]);
                if (state.IsComplete)
                    EmitSection(pid, state);
            }

            // Start new section after pointer_field
            payload = payload[pointer..];
            state.Reset();
            AccumulatePayload(pid, state, payload);
        }
        else
        {
            if (!state.Accumulating) return;
            AccumulatePayload(pid, state, payload);
        }
    }

    private void AccumulatePayload(int pid, SectionState state, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            if (!state.Accumulating)
            {
                // Stuffing bytes (0xFF) or end of useful data
                if (payload[offset] == 0xFF) return;

                // Start a new section: need at least 3 bytes for header
                int remaining = payload.Length - offset;
                if (remaining < 3) return;

                byte tableId = payload[offset];
                bool ssi = (payload[offset + 1] & 0x80) != 0;
                int sectionLen = ((payload[offset + 1] & 0x0F) << 8) | payload[offset + 2];
                if (sectionLen > 1021) { offset++; continue; } // invalid

                state.Start(tableId, sectionLen + 3); // +3 for the 3-byte header we already read
                state.Append(payload[offset..Math.Min(offset + state.TotalLength, payload.Length)]);
                int consumed = Math.Min(state.TotalLength, remaining);
                offset += consumed;

                if (state.IsComplete)
                {
                    EmitSection(pid, state);
                    state.Reset();
                }
            }
            else
            {
                int needed = state.TotalLength - state.BufferLength;
                int avail = payload.Length - offset;
                int take = Math.Min(needed, avail);
                state.Append(payload.Slice(offset, take));
                offset += take;

                if (state.IsComplete)
                {
                    EmitSection(pid, state);
                    state.Reset();
                }
            }
        }
    }

    private void EmitSection(int pid, SectionState state)
    {
        SectionReady?.Invoke(pid, state.Buffer.ToArray());
    }

    private sealed class SectionState
    {
        public bool Accumulating { get; private set; }
        public int TotalLength { get; private set; }
        public int BufferLength => _buffer.Count;
        public byte TableId { get; private set; }

        private readonly List<byte> _buffer = new(1024);

        public ReadOnlySpan<byte> Buffer => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer);
        public bool IsComplete => Accumulating && _buffer.Count >= TotalLength;

        public void Start(byte tableId, int totalLength)
        {
            _buffer.Clear();
            TableId = tableId;
            TotalLength = totalLength;
            Accumulating = true;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            int needed = TotalLength - _buffer.Count;
            if (needed <= 0) return;
            var slice = data.Length <= needed ? data : data[..needed];
            foreach (var b in slice) _buffer.Add(b);
        }

        public void Reset()
        {
            _buffer.Clear();
            Accumulating = false;
            TotalLength = 0;
        }
    }
}
