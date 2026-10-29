namespace BattleArena
{
    /// <summary>TCP 패킷 종류</summary>
    public static class PacketType
    {
        // 클라이언트 → 서버
        public const int LOGIN = 1;
        public const int CHAT = 2;

        // 서버 → 클라이언트
        public const int WELCOME = 11;
        public const int CHAT_ECHO = 12;
        public const int PLAYER_IN = 20;
        public const int PLAYER_OUT = 21;
    }

    /// <summary>UDP 패킷 종류 (문자열 기반)</summary>
    public static class UdpPacket
    {
        public const string MOVE = "MOVE";
        public const string STATE = "STATE";
    }

    public class LoginPacket
    {
        public int Type { get; set; } = PacketType.LOGIN;
        public string Nickname { get; set; } = "";
    }

    public class ChatPacket
    {
        public int Type { get; set; } = PacketType.CHAT;
        public string Message { get; set; } = "";
    }

    public class WelcomePacket
    {
        public int Type { get; set; } = PacketType.WELCOME;
        public int PlayerId { get; set; }
        public string Message { get; set; } = "";
    }

    public class ChatEchoPacket
    {
        public int Type { get; set; } = PacketType.CHAT_ECHO;
        public string Nickname { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class NoticePacket
    {
        public int Type { get; set; }
        public string Message { get; set; } = "";
    }
}
