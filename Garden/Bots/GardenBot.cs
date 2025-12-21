namespace Garden.Bots
{
    /// <summary>
    /// Bot for Garden game.
    /// To use: In Runner.cs, change the bot line to:
    ///   BotBase bot = new GardenBot();
    /// </summary>
    public class GardenBot : BotBase
    {
        public override void HandleState(string stateName)
        {
            // Bot logic for Garden game
            // This is called every frame when a state is detected

            switch (stateName)
            {
                case "lockscreen":
                    SwipeFromRoi("lock", 208, 492);
                    break;
                case "firstscreen":
                    RunAction("scrollright");
                    break;
                case "secondscreen":
                    Click("mirrativicon");
                    break;
                case "needtopressokbutton":
                    Click("okbutton");
                    break;
                case "startmirrativ":
                    Click("livegamebutton");
                    break;
                case "livegamepage":
                    break;
                case "gardenmain":
                    break;
                case "unknown":
                    break;

            }
        }
    }
}
