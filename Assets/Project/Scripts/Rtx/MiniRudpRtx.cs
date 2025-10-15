// Assets/Scripts/Networking/MiniRudpRtx.cs
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

public class MiniRudpRtx
{
    // == ヘッダ仕様 ==
    // [0..1]   Magic(0xCAFE)
    // [2]      Ver(1)
    // [3]      Flags(未使用)
    // [4..5]   ConnId
    // [6..7]   Seq          <-- 送信パケットの連番
    // [8..9]   Ack          <-- 今回は未使用（将来拡張用）
    // [10..11] Len          <-- MsgType + Payload の長さ
    // [12..13] CRC16-CCITT
    // [14]     MsgType
    // [15..]   Payload

    private const ushort MAGIC = 0xCAFE;
    private const byte VERSION = 1;

    // メッセージ種別
    private const byte MSG_ACK  = 0;
    private const byte MSG_DATA = 1; // 利用側のアプリ・メッセージもこの下にぶら下げ可

    private readonly UdpClient _udp;
    private IPEndPoint _remote;
    private ushort _nextSeq;

    // 送信中パケット管理
    private class Pending
    {
        public ushort Seq;
        public byte[] Packet;        // 実送信バイト列（再送でそのまま再送）
        public int Length;
        public long LastSentMs;
        public int RtoMs;            // 再送タイマ(ms)
        public int Retries;          // 再送回数
    }

    private readonly Dictionary<ushort, Pending> _pending = new();
    private readonly List<ushort> _toRemove = new(); // Tickでの削除用

    // RTO関連既定値（会場を想定して控えめ）
    public int InitialRtoMs = 60;           // 初期RTO
    public int MaxRtoMs     = 800;          // バックオフ上限
    public int MaxRetries   = 8;            // 最大再送回数
    public float Backoff    = 1.6f;         // 乗数バックオフ

    // アプリに渡す受信ハンドラ
    public Action<byte, ArraySegment<byte>> OnData; // (appMsgType, payload)

    // 受信重複排除（必要最低限）
    private ushort _lastDeliveredSeq;
    private readonly HashSet<ushort> _recent = new(); // ごく簡易。必要ならLRUに。

    public MiniRudpRtx(string remoteHost, int remotePort, int localPort = -1)
    {
        _udp = (localPort >= 0)
            ? new UdpClient(new IPEndPoint(IPAddress.Any, localPort))
            : new UdpClient();

        _remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
        _udp.Client.ReceiveTimeout = 1; // 非ブロッキング気味
    }

    public void SetRemote(string host, int port)
    {
        _remote = new IPEndPoint(IPAddress.Parse(host), port);
    }

    // ---- 送信（アプリ用） -------------------------------------------------

    // アプリ用: 任意の appMsgType + payload を送信（信頼配送）
    public void SendReliable(byte appMsgType, ReadOnlySpan<byte> payload)
    {
        ushort seq = _nextSeq++;
        var packet = BuildPacket(MSG_DATA, seq, appMsgType, payload, out int totalLen);

        // 即時送信
        _udp.Send(packet, totalLen, _remote);

        // 未ACKキューへ
        _pending[seq] = new Pending
        {
            Seq       = seq,
            Packet    = packet,
            Length    = totalLen,
            LastSentMs= NowMs(),
            RtoMs     = InitialRtoMs,
            Retries   = 0
        };
    }

    // ---- 受信ポーリング ---------------------------------------------------

    public void PollReceive()
    {
        while (_udp.Available > 0)
        {
            IPEndPoint ep = null;
            var buf = _udp.Receive(ref ep);
            if (!ValidateAndParse(buf, out ushort seq, out byte msgType, out byte appMsgType, out ArraySegment<byte> payload))
                continue;

            if (msgType == MSG_ACK)
            {
                // ACK受信: 該当Seqを確定
                if (_pending.Remove(seq)) { /* OK */ }
                continue;
            }

            if (msgType == MSG_DATA)
            {
                // 重複排除（雑に）
                if (IsDuplicate(seq)) { SendAck(seq); continue; }

                // アプリへコールバック
                OnData?.Invoke(appMsgType, payload);

                // 即ACK返し
                SendAck(seq);
            }
        }
    }

    // ---- 再送タイマ -------------------------------------------------------

    public void TickRetransmit()
    {
        if (_pending.Count == 0) return;

        long now = NowMs();
        _toRemove.Clear();

        foreach (var kv in _pending)
        {
            var p = kv.Value;
            if (now - p.LastSentMs >= p.RtoMs)
            {
                if (p.Retries >= MaxRetries)
                {
                    // ドロップ（必要ならイベント通知）
                    _toRemove.Add(p.Seq);
                    continue;
                }

                // 再送
                _udp.Send(p.Packet, p.Length, _remote);
                p.LastSentMs = now;
                p.Retries++;
                p.RtoMs = Math.Min((int)(p.RtoMs * Backoff), MaxRtoMs);
            }
        }

        if (_toRemove.Count > 0)
        {
            foreach (var seq in _toRemove) _pending.Remove(seq);
        }
    }

    // ---- 後始末 -----------------------------------------------------------

    public void Close() => _udp?.Close();

    // ---- 内部：パケット構築/解析 ------------------------------------------

    private byte[] BuildPacket(byte msgType, ushort seq, byte appMsgType, ReadOnlySpan<byte> payload, out int totalLen)
    {
        int len = 15 + 1 + payload.Length; // ヘッダ15 + MsgType(1) + payload
        var p = new byte[len];

        // 固定ヘッダ
        p[0] = (byte)((MAGIC >> 8) & 0xFF);  // 上位バイト
        p[1] = (byte)(MAGIC & 0xFF);         // 下位バイト
        p[2] = VERSION;
        p[3] = 0;                   // Flags
        WriteU16(p, 4, 1);          // ConnId=1
        WriteU16(p, 6, seq);        // Seq
        WriteU16(p, 8, 0);          // Ack(未使用)
        WriteU16(p,10, (ushort)(1 + payload.Length)); // Len
        WriteU16(p,12, 0);          // CRC(一旦0)

        // 上位のメッセージ種別
        p[14] = msgType;

        // アプリメッセージ種別 + Payload
        p[15] = appMsgType;
        if (payload.Length > 0)
            payload.CopyTo(new Span<byte>(p, 16, payload.Length));

        // CRC (先頭〜末尾まで)
        ushort crc = Crc16(p.AsSpan(0, len));
        WriteU16(p, 12, crc);

        totalLen = len;
        return p;
    }

    private bool ValidateAndParse(byte[] buf, out ushort seq, out byte msgType, out byte appMsgType, out ArraySegment<byte> payload)
    {
        seq = 0; msgType = 0; appMsgType = 0; payload = default;

        if (buf.Length < 16) return false;
        if (buf[0] != (byte)((MAGIC >> 8) & 0xFF) || buf[1] != (byte)(MAGIC & 0xFF)) return false;
        if (buf[2] != VERSION) return false;

        // CRCチェック
        ushort recv = ReadU16(buf, 12);
        buf[12] = 0; buf[13] = 0;
        ushort calc = Crc16(buf.AsSpan(0, buf.Length));
        if (recv != calc) return false;

        seq = ReadU16(buf, 6);
        msgType = buf[14];
        appMsgType = buf[15];

        int payLen = buf.Length - 16;
        if (payLen < 0) payLen = 0;
        payload = new ArraySegment<byte>(buf, 16, payLen);
        return true;
    }

    private void SendAck(ushort seq)
    {
        // ACKはアプリMsgType=0/ペイロード空
        var packet = BuildPacket(MSG_ACK, seq, 0, ReadOnlySpan<byte>.Empty, out int len);
        _udp.Send(packet, len, _remote);
    }

    private bool IsDuplicate(ushort seq)
    {
        if (seq == _lastDeliveredSeq) return true;
        if (_recent.Contains(seq)) return true;

        _recent.Add(seq);
        _lastDeliveredSeq = seq;

        // 簡易掃除（雑でOK：一定サイズ超えたらクリア）
        if (_recent.Count > 256) _recent.Clear();
        return false;
    }

    // ---- Utils ------------------------------------------------------------

    private static long NowMs() => (long)(Time.realtimeSinceStartup * 1000.0f);

    private static void WriteU16(byte[] p, int ofs, ushort v) { p[ofs] = (byte)(v >> 8); p[ofs + 1] = (byte)v; }
    private static ushort ReadU16(byte[] p, int ofs) => (ushort)((p[ofs] << 8) | p[ofs + 1]);

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ 0x1021 : (crc << 1));
        }
        return crc;
    }
}
