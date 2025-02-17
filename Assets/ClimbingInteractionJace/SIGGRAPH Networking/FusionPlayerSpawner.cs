using Fusion;
using UnityEngine;

public class PlayerSpawner : SimulationBehaviour, IPlayerJoined
{
    public NetworkedHands otherPlayerPrefab;

    public void PlayerJoined(PlayerRef player)
    {
        if (player == Runner.LocalPlayer)
        {
            NetworkedHands otherPlayerObject = Runner.Spawn(otherPlayerPrefab, new Vector3(0, 0, 0), Quaternion.identity);
        }
    }
}