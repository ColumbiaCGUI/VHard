using System;
using System.Collections.Generic;

public sealed class OverlapContactResolver<T> where T : class
{
    private sealed class Contact
    {
        public int Count;
        public long EnterOrder;
    }

    private readonly Dictionary<T, Contact> contacts;
    private readonly IEqualityComparer<T> comparer;
    private long nextEnterOrder;

    public OverlapContactResolver(IEqualityComparer<T> comparer = null)
    {
        this.comparer = comparer ?? EqualityComparer<T>.Default;
        contacts = new Dictionary<T, Contact>(this.comparer);
    }

    public T Current { get; private set; }
    public int ContactCount => contacts.Count;

    public bool Enter(T contact)
    {
        if (contact == null)
        {
            throw new ArgumentNullException(nameof(contact));
        }

        T previous = Current;
        if (contacts.TryGetValue(contact, out Contact state))
        {
            state.Count++;
        }
        else
        {
            contacts.Add(contact, new Contact { Count = 1, EnterOrder = ++nextEnterOrder });
            Current = contact;
        }
        return !comparer.Equals(previous, Current);
    }

    public bool Exit(T contact)
    {
        if (contact == null || !contacts.TryGetValue(contact, out Contact state))
        {
            return false;
        }

        T previous = Current;
        state.Count--;
        if (state.Count <= 0)
        {
            contacts.Remove(contact);
            if (comparer.Equals(Current, contact))
            {
                SelectNewestContact();
            }
        }
        return !comparer.Equals(previous, Current);
    }

    public bool Remove(T contact)
    {
        if (contact == null || !contacts.Remove(contact))
        {
            return false;
        }

        T previous = Current;
        if (comparer.Equals(Current, contact))
        {
            SelectNewestContact();
        }
        return !comparer.Equals(previous, Current);
    }

    public int GetOverlapCount(T contact)
    {
        return contact != null && contacts.TryGetValue(contact, out Contact state) ? state.Count : 0;
    }

    public void Clear()
    {
        contacts.Clear();
        Current = null;
    }

    private void SelectNewestContact()
    {
        Current = null;
        long newestOrder = long.MinValue;
        foreach (KeyValuePair<T, Contact> pair in contacts)
        {
            if (pair.Value.EnterOrder > newestOrder)
            {
                Current = pair.Key;
                newestOrder = pair.Value.EnterOrder;
            }
        }
    }
}
