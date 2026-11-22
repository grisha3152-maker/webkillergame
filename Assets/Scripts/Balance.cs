using System;
using UnityEngine;

/// <summary>
/// Числа из Assets/Data/balance.json. Рантайм грузит TextAsset через Resources.
/// </summary>
public static class Balance
{
    static BalanceData data;
    static bool loaded;

    public static BalanceData Data
    {
        get
        {
            EnsureLoaded();
            return data;
        }
    }

    public static void EnsureLoaded()
    {
        if (loaded && data != null) return;
        loaded = true;

        TextAsset asset = Resources.Load<TextAsset>("balance");
#if UNITY_EDITOR
        if (asset == null)
            asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/balance.json");
#endif
        if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
        {
            data = JsonUtility.FromJson<BalanceData>(asset.text);
            if (data != null)
            {
                data.Normalize();
                return;
            }
        }

        Debug.LogWarning("Balance: не найден balance.json, используем значения по умолчанию из GDD 15.3.");
        data = BalanceData.CreateDefault();
    }
}

[Serializable]
public class BalanceData
{
    public float playSeconds = 480f;
    public float hitRadiusInHeroWidths = 1.5f;
    public float coneDegrees = 56f;
    public bool hitClearsAllInCone = true;
    public float baseTurnDegreesPerSecond = 240f;
    public float turnAfterPhone = 0.55f;
    public float turnAfterHit = 0.4f;
    public float turnFloor = 0.35f;
    public int killsToRestoreTurn = 5;
    public int dollarsPerKill = 10;
    public float pillWindowSeconds = 20f;
    public float pillWarningSeconds = 5f;
    public float pillSpeedMultiplier = 1.6f;
    public float pillSpawnIntervalMultiplier = 0.65f;
    public float messageIgnoreSeconds = 8f;
    public RelationStart relationStart = new RelationStart();
    public int relationWarm = 15;
    public int relationCold = -25;
    public int farewellStrikes = 2;
    public int maxShadows = 5;
    public float forcedHitLockSeconds = 0.75f;
    public float forcedHitTravelSeconds = 0.55f;
    public float forcedHitStartRadiusScale = 1.15f;
    public string[] familyContacts = { "mom", "anya", "lev", "mila" };
    public EraBalance[] eras;
    public int[] roomThresholds = { 0, 200, 500, 900, 1400, 2200 };
    public SpawnWeights spawnWeights = new SpawnWeights();

    public float SectorHalfAngle => coneDegrees * 0.5f;

    public float SpawnEveryAt(float elapsed)
    {
        if (eras == null || eras.Length == 0) return 4f;
        for (int i = 0; i < eras.Length; i++)
        {
            if (elapsed <= eras[i].until)
                return Mathf.Max(0.1f, eras[i].spawnEvery);
        }
        return Mathf.Max(0.1f, eras[eras.Length - 1].spawnEvery);
    }

    public void Normalize()
    {
        if (relationStart == null) relationStart = new RelationStart();
        if (spawnWeights == null) spawnWeights = new SpawnWeights();
        if (eras == null || eras.Length == 0) eras = CreateDefault().eras;
        if (roomThresholds == null || roomThresholds.Length == 0)
            roomThresholds = new[] { 0, 200, 500, 900, 1400, 2200 };
        if (familyContacts == null || familyContacts.Length == 0)
            familyContacts = new[] { "mom", "anya", "lev", "mila" };
        if (coneDegrees <= 0f) coneDegrees = 56f;
        if (hitRadiusInHeroWidths <= 0f) hitRadiusInHeroWidths = 1.5f;
        if (baseTurnDegreesPerSecond <= 0f) baseTurnDegreesPerSecond = 240f;
        if (killsToRestoreTurn <= 0) killsToRestoreTurn = 5;
        if (dollarsPerKill <= 0) dollarsPerKill = 10;
        if (maxShadows <= 0) maxShadows = 5;
        if (turnFloor <= 0f) turnFloor = 0.35f;
    }

    public static BalanceData CreateDefault()
    {
        var d = new BalanceData
        {
            eras = new[]
            {
                new EraBalance { id = "14", until = 120f, ghostSpeed = 1f, spawnEvery = 4f },
                new EraBalance { id = "20", until = 200f, ghostSpeed = 1.25f, spawnEvery = 3.2f },
                new EraBalance { id = "30", until = 380f, ghostSpeed = 1.7f, spawnEvery = 2.2f },
                new EraBalance { id = "50", until = 480f, ghostSpeed = 2.3f, spawnEvery = 1.5f }
            },
            spawnWeights = new SpawnWeights { left = 0.35f, right = 0.35f, top = 0.3f },
            relationStart = new RelationStart { mom = 60, anya = 40, lev = 45, mila = 50 }
        };
        d.Normalize();
        return d;
    }
}

[Serializable]
public class RelationStart
{
    public int mom = 60;
    public int anya = 40;
    public int lev = 45;
    public int mila = 50;
}

[Serializable]
public class EraBalance
{
    public string id;
    public float until;
    public float ghostSpeed = 1f;
    public float spawnEvery = 4f;
}

[Serializable]
public class SpawnWeights
{
    public float left = 0.35f;
    public float right = 0.35f;
    public float top = 0.3f;
}
