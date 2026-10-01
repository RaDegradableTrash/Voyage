namespace Voyage.Exploration
{
    public sealed class VehicleInteractable : WorldInteractable
    {
        public override void Interact(ExplorerPlayer player)
        {
            if(ExplorationSession.Instance!=null)ExplorationSession.Instance.EnterVehicle();
            base.Interact(player);
        }
    }
}
