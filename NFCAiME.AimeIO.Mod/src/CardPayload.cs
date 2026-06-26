namespace NFCAiME.AimeIO.Mod
{
    internal sealed class CardPayload
    {
        public string Type { get; set; }
        public string PrivateAccessCode { get; set; }
        public string OfficialAccessCode { get; set; }
        public string Idm { get; set; }
        public bool Encrypted { get; set; }
    }
}
