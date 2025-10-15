// Assets/Scripts/Networking/MiniRudp.cs
using System;
using System.Net;
using System.Net.Sockets;

public class MiniRudp
{
    private readonly UdpClient _udp;
    private IPEndPoint _remote;
    private ushort _seq, _acked;
    private readonly byte[] _sendBuf = new byte[1500];

    public MiniRudp(string remoteHost, int remotePort, int localPort = -1)
    {
        if (localPort >= 0)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        }
        else
        {
            _udp = new UdpClient(); // OSが空きポートを割り当て
        }

        _remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
        _udp.Client.ReceiveTimeout = 1; // 非ブロッキング気味に（PollReceiveを毎フレーム回す想定）
    }

    public void SetRemote(string host, int port)
    {
        _remote = new IPEndPoint(IPAddress.Parse(host), port);
    }

    public void Send(byte msgType, ReadOnlySpan<byte> payload)
    {
        // 超簡易ヘッダ: Magic(2) Ver(1) Flags(1) ConnId(2) Seq(2) Ack(2) Len(2) CRC(2) MsgType(1) + Payload
        Span<byte> p = _sendBuf.AsSpan();
        p[0] = 0xCA; p[1] = 0xFE; // Magic
        p[2] = 1;                 // Ver
        p[3] = 0;                 // Flags
        WriteU16(p, 4, 1);        // ConnId = 1（単一接続想定の例）
        WriteU16(p, 6, _seq);     // Seq
        WriteU16(p, 8, _acked);   // Ack（本来は受信処理で更新）
        WriteU16(p,10, (ushort)(payload.Length + 1)); // Len(含MsgType)
        // CRCは一旦 0 を入れてから計算
        WriteU16(p,12, 0);
        p[14] = msgType;

        payload.CopyTo(p.Slice(15));

        // CRCは先頭〜(MsgType+Payload)まで
        ushort crc = Crc16(p.Slice(0, 15 + payload.Length));
        WriteU16(p,12, crc);

        int totalLen = 15 + payload.Length;
        _udp.Send(_sendBuf, totalLen, _remote);
        _seq++;
    }

    public void PollReceive(Action<byte, ArraySegment<byte>> onMsg)
    {
        while (_udp.Available > 0)
        {
            IPEndPoint ep = null;
            var buf = _udp.Receive(ref ep);
            if (buf.Length < 15) continue;
            if (buf[0] != 0xCA || buf[1] != 0xFE) continue;

            // CRCチェック
            ushort recvCrc = ReadU16(buf, 12);
            buf[12] = 0; buf[13] = 0; // いったん0にして再計算
            ushort calc = Crc16(buf.AsSpan(0, buf.Length));
            if (recvCrc != calc) continue; // 破損は捨てる

            byte msgType = buf[14];
            int payloadLen = buf.Length - 15;
            var payload = new ArraySegment<byte>(buf, 15, payloadLen);

            // 簡易Ack更新（本来は受信Seqを見て累積ACKや選択ACKを更新する）
            _acked = ReadU16(buf, 6);

            onMsg?.Invoke(msgType, payload);
        }
    }

    public void Close()
    {
        _udp?.Close();
    }

    static void WriteU16(Span<byte> p, int ofs, ushort v) { p[ofs] = (byte)(v >> 8); p[ofs + 1] = (byte)v; }
    static ushort ReadU16(byte[] p, int ofs) => (ushort)((p[ofs] << 8) | p[ofs + 1]);

    static ushort Crc16(ReadOnlySpan<byte> data)
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
