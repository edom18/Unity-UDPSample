// Assets/Scripts/Networking/RtxClient.cs
using System;
using System.Collections;
using System.Text;
using UnityEngine;

public class RtxClient : MonoBehaviour
{
    public string serverHost = "127.0.0.1";
    public int serverPort = 9000;
    public int listenPort = 9001;

    private MiniRudpRtx _rtx;
    private float _timer;

    private const byte AppText = 10;
    private bool _isInitialized = false;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(1f); // サーバ起動待ち
        
        _rtx = new MiniRudpRtx(serverHost, serverPort, localPort: listenPort);
        _rtx.OnData = (appType, payload) =>
        {
            if (appType == AppText)
            {
                string s = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);
                Debug.Log($"[Client] from server: {s}");
            }
        };

        _rtx.SendReliable(AppText, Encoding.UTF8.GetBytes("Hello (reliable)"));

        _isInitialized = true;
    }

    private void Update()
    {
        if (!_isInitialized) return;
        
        _rtx.PollReceive();
        _rtx.TickRetransmit();

        _timer += Time.deltaTime;
        if (_timer >= 3f)
        {
            _timer = 0f;
            _rtx.SendReliable(AppText, Encoding.UTF8.GetBytes("Ping " + DateTime.Now.ToString("T")));
        }
    }

    private void OnDestroy() => _rtx?.Close();
}