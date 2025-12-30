namespace Garden.Bots
{
    public class GardenBot : BotBase
    {
        public override void HandleState(string stateName)
        {
            // Bot logic for Garden game
            // This is called every frame when a state is detected

            switch (stateName)
            {
                case "1lockscreen":
                    QueueAction("lock", "dragup");
                    break;
                case "2firstscreen":
                    QueueAction("dragleft");
                    break;
            }
        }
    }
}
