namespace PlayView.Services;

public static class OttCatalog
{
    public static List<OttServiceConfig> DefaultServices() => new()
    {
        new() { Id = "hotstar", Name = "JioHotstar",   Url = "https://www.hotstar.com" },
        new() { Id = "netflix", Name = "Netflix",      Url = "https://www.netflix.com" },
        new() { Id = "prime",   Name = "Prime Video",  Url = "https://www.primevideo.com" },
        new() { Id = "sonyliv", Name = "SonyLIV",      Url = "https://www.sonyliv.com" },
        new() { Id = "zee5",    Name = "ZEE5",         Url = "https://www.zee5.com" },
        new() { Id = "sunnxt",  Name = "Sun NXT",      Url = "https://www.sunnxt.com" },
        new() { Id = "etvwin",  Name = "ETV Win",      Url = "https://www.etvwin.com" },
    };
}
