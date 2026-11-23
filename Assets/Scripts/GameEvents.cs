using UnityEngine;
using System;

public static class GameEvents
{
    public static event Action<int> OnGhostKilled;
    public static event Action OnGhostReachedHero;
    public static event Action OnHeroHit;
    public static event Action<float> OnAgeChanged;
    public static event Action<int> OnMoneyChanged;
    public static event Action<int> OnShadowCountChanged;
    public static event Action<bool> OnMessageReplied;
    public static event Action OnFinaleStarted;
    public static event Action OnComboRestored;
    /// <summary>Часы запущены (StartClock) — fallback для таймера телефона.</summary>
    public static event Action OnClockStarted;
    /// <summary>Посадка / конец opening (EnableCombat) — основной старт пингов.</summary>
    public static event Action OnDeskReady;
    // Удар Z/X/C: старт / срыв / успех последовательности над одним призраком.
    public static event Action OnHitComboStarted;
    public static event Action OnHitComboFailed;
    public static event Action OnHitComboSucceeded;
    /// <summary>Новое входящее уведомление телефона (пинг стал активным).</summary>
    public static event Action<string> OnMessageArrived;
    /// <summary>Короткая реплика под ГГ при открытии телефона с unread (пинги 1–9); пустая строка не шлётся.</summary>
    public static event Action<string> OnHeroMutter;

    public static void GhostKilled(int payout) => OnGhostKilled?.Invoke(payout);

    public static void GhostReachedHero()
    {
        Debug.Log("GhostReachedHero");
        OnGhostReachedHero?.Invoke();
        // GDD: призрак дошёл до гг → попадание (тень в углу + штраф поворота).
        HeroHit();
    }

    public static void HeroHit() => OnHeroHit?.Invoke();
    public static void AgeChanged(float age) => OnAgeChanged?.Invoke(age);
    public static void MoneyChanged(int money) => OnMoneyChanged?.Invoke(money);
    public static void ShadowCountChanged(int count) => OnShadowCountChanged?.Invoke(count);
    public static void MessageReplied(bool positive) => OnMessageReplied?.Invoke(positive);
    public static void FinaleStarted() => OnFinaleStarted?.Invoke();
    public static void ComboRestored() => OnComboRestored?.Invoke();
    public static void ClockStarted() => OnClockStarted?.Invoke();
    public static void DeskReady() => OnDeskReady?.Invoke();
    public static void HitComboStarted() => OnHitComboStarted?.Invoke();
    public static void HitComboFailed() => OnHitComboFailed?.Invoke();
    public static void HitComboSucceeded() => OnHitComboSucceeded?.Invoke();
    public static void MessageArrived(string contactId) => OnMessageArrived?.Invoke(contactId);
    public static void HeroMutter(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        OnHeroMutter?.Invoke(line);
    }
}
