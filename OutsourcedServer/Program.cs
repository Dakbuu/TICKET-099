using System;
using System.Threading.Tasks;

namespace BattleArena
{
    public static class Program
    {
        public static async Task Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════════╗");
            Console.WriteLine("║   배틀아레나 서버  (외주 납품본 v0.9)      ║");
            Console.WriteLine("║   TICKET-099  인수 검토 대상               ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            StudentConfig.Verify();

            GameServer server = new GameServer();

            try
            {
                await server.StartAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[서버 종료] {ex.GetType().Name} : {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("아무 키나 누르면 창이 닫힙니다.");
                Console.ReadKey();
            }
        }
    }
}
