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
        if (Runner.SessionInfo.PlayerCount == 1)
        {
            FindAnyObjectByType<SceneConfiguror>().shouldOtherPlayerHandsBeActive = false;
        }
        else if (Runner.SessionInfo.PlayerCount == 2)
        {
            FindAnyObjectByType<SceneConfiguror>().shouldOtherPlayerHandsBeActive = true;

        }
    }
}