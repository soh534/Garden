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
                    StateDetector.NextExpectedState = "2firstscreen";
                    break;
                case "2firstscreen":
                    QueueAction("dragleft");
                    StateDetector.NextExpectedState = "3secondscreen";
                    break;
                case "3secondscreen":
                    QueueAction("click", "mirrativicon");
                    StateDetector.NextExpectedState = "4firstmirrativad";
                    break;
                case "4firstmirrativad":
                    QueueAction("click", "batsu");
                    StateDetector.NextExpectedState = "5firstmirrativadclosed";
                    break;
                case "5firstmirrativadclosed":
                    QueueAction("click", "livegameicon");
                    StateDetector.NextExpectedState = "6livegamepage";
                    break;
                case "6livegamepage":
                    QueueAction("scrollup");
                    StateDetector.NextExpectedState = "7livegamepagewithgarden";
                    break;
                case "7livegamepagewithgarden":
                    QueueAction("click", "gardenicon");
                    StateDetector.NextExpectedState = "8gardeniconclicked";
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
