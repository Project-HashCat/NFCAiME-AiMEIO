using System;
using System.Threading;
using MelonLoader;
using WebSocketDotNet;

namespace NFCAiME.AimeIO.Mod
{
    internal sealed class RelayClient
    {
        private readonly ModConfig _config;
        private Thread _thread;
        private volatile bool _running;
        private WebSocket _socket;

        public RelayClient(ModConfig config)
        {
            _config = config;
        }

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "NFCAiME AiMEIO Relay"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_socket != null)
                {
                    _socket.SendClose(WebSocketCloseCode.ClosedOk, "mod stopping");
                }
            }
            catch
            {
            }
        }

        private void Run()
        {
            while (_running)
            {
                try
                {
                    var endpoint = _config.BuildWebSocketUrl();
                    MelonLogger.Msg("[NFCAiME] connecting relay: " + MaskEndpoint(endpoint));

                    _socket = new WebSocket(endpoint, new WebSocketConfiguration
                    {
                        AutoConnect = false,
                        UseAutomaticReceiveThread = true
                    });
                    _socket.Opened += OnOpened;
                    _socket.Closed += OnClosed;
                    _socket.TextReceived += OnTextReceived;
                    _socket.Connect();

                    while (_running && _socket != null && _socket.State != WebSocketState.Closed)
                    {
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[NFCAiME] relay disconnected: " + ex.Message);
                }

                if (_running)
                {
                    Thread.Sleep(3000);
                }
            }
        }

        private void OnOpened()
        {
            MelonLogger.Msg("[NFCAiME] relay online");
        }

        private void OnClosed(WebSocketCloseCode code, string reason)
        {
            MelonLogger.Warning("[NFCAiME] relay closed: " + code + " " + reason);
        }

        private void OnTextReceived(string text)
        {
            try
            {
                var payload = PayloadParser.Parse(text);
                if (!string.Equals(payload.Type, "card", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(payload.Type, "aime", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(payload.Type, "felica", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(payload.PrivateAccessCode) &&
                    string.IsNullOrWhiteSpace(payload.OfficialAccessCode) &&
                    string.IsNullOrWhiteSpace(payload.Idm))
                {
                    return;
                }

                ResolveAimeId(payload);
                CardCache.Store(payload);
                MelonLogger.Msg("[NFCAiME] cached card payload");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[NFCAiME] ignored invalid payload: " + ex.Message);
            }
        }

        private void ResolveAimeId(CardPayload payload)
        {
            if (payload.AimeId != 0)
            {
                return;
            }

            uint aimeId;
            string accessCode;
            string reason;
            if (!AimeDbResolver.TryResolve(payload, _config, out aimeId, out accessCode, out reason))
            {
                MelonLogger.Warning("[NFCAiME] AimeDB userId resolve skipped/failed: " + reason);
                return;
            }

            payload.AimeId = aimeId;
            if (string.IsNullOrWhiteSpace(payload.PrivateAccessCode) && string.IsNullOrWhiteSpace(payload.OfficialAccessCode))
            {
                payload.PrivateAccessCode = accessCode;
            }

            MelonLogger.Msg("[NFCAiME] resolved aimeId via AimeDB: " + aimeId);
        }

        private static string MaskEndpoint(string endpoint)
        {
            var idx = endpoint.LastIndexOf('/');
            if (idx < 0 || idx + 10 >= endpoint.Length)
            {
                return endpoint;
            }

            var prefix = endpoint.Substring(0, idx + 1);
            var key = endpoint.Substring(idx + 1);
            return prefix + key.Substring(0, Math.Min(10, key.Length)) + "***";
        }
    }
}
