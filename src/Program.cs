using Garden;

class Program
{
    static void Main(string[] args)
    {
        // Offline harnesses (see Cli): the live pipelines over saved crops/frames, then exit.
        if (args.Length >= 2 && args[0] == "--ocr-read")
        {
            Cli.OcrRead(args[1], args.Length > 2 ? args[2] : null);
            return;
        }
        if (args.Length >= 3 && args[0] == "--find-all")
        {
            Cli.FindAll(args[1], args[2]);
            return;
        }
        Runner runner = new();
        runner.Run();
    }
}