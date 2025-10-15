using System.Text;
using UnityEngine;

public class RtxServer : MonoBehaviour
{
    public int listenPort = 9000;
    public string clientHost = "127.0.0.1";
    public int clientPort = 9001;

    private MiniRudpRtx _rtx;

    // アプリケーション層でさらにタイプを分けたい場合は appMsgType を自前管理
    private const byte AppText = 10;

    void Start()
    {
        _rtx = new MiniRudpRtx(clientHost, clientPort, localPort: listenPort);
        _rtx.OnData = (appType, payload) =>
        {
            if (appType == AppText)
            {
                string s = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);
                Debug.Log($"[Server] recv: {s}");
                _rtx.SendReliable(AppText, Encoding.UTF8.GetBytes("[Echo] " + s));
            }
        };

        Debug.Log($"[Server] listening {listenPort}, default remote {clientHost}:{clientPort}");
    }

    void Update()
    {
        _rtx.PollReceive();
        _rtx.TickRetransmit();
    }

    void OnDestroy() => _rtx?.Close();
}