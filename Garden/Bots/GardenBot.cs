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
                case "lockscreen":
                    QueueAction("lock", "dragup");
                    break;
                case "firstscreen":
                    QueueAction("scrollright");
                    break;
                case "secondscreen":
                    QueueAction("mirrativicon", "click");
                    break;
                case "needtopressokbutton":
                    QueueAction("okbutton", "click");
                    break;
                case "startmirrativ":
                    QueueAction("livegamebutton", "click");
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
