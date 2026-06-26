using MelonLoader;

namespace NFCAiME.AimeIO.Mod
{
    public sealed class NFCAimeAimeIoMod : MelonMod
    {
        private RelayClient _client;

        public override void OnInitializeMelon()
        {
            var config = ModConfig.Load();
            if (!config.Enabled)
            {
                MelonLogger.Msg("[NFCAiME] disabled");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.SessionKey))
            {
                MelonLogger.Warning("[NFCAiME] session-key is empty. Fill segatools.ini [nfcaimeio] or Mods/NFCAiME.AimeIO.Mod.toml");
                return;
            }

            CardCache.Configure(config);
            AimeReaderPatches.Apply(new HarmonyLib.Harmony("me.segasb.nfcaime.aimeio"));

            _client = new RelayClient(config);
            _client.Start();
        }

        public override void OnApplicationQuit()
        {
            if (_client != null)
            {
                _client.Stop();
            }
        }
    }
}
