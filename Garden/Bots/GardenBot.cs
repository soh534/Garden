namespace Garden.Bots
{
    public class GardenBot : BotBase
    {
        public override void QueueStateResponse()
        {
            // Bot logic for Garden game
            // This is called every frame when a state is detected

            switch (StateDetector.CurrentState)
            {
                case "1lockscreen":
                    QueueAction("dragup", "lock");
                    break;
                case "2firstscreen":
                    QueueAction("dragleft");
                    break;
                case "3secondscreen":
                    QueueAction("click", "mirrativicon");
                    break;
                case "4firstmirrativad":
                    QueueAction("click", "batsu");
                    break;
                case "5firstmirrativadclosed":
                    QueueAction("click", "livegameicon");
                    break;
                case "6livegamepage":
                    QueueAction("scrollup");
                    break;
                case "7livegamepagewithgarden":
                    QueueAction("click", "gardenicon");
                    break;
                case "8gardeniconclicked":
                    QueueAction("click", "playbutton");
                    break;
                default:
                    break;
            }
        }
    }
}
