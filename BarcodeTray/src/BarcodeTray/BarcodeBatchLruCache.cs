using System;
using System.Collections.Generic;

namespace BarcodeTray;

/// <summary>
/// Cache « le moins récemment utilisé » à capacité fixe, pour les miniatures de la fenêtre « Codes-barres en série » : le tableau
/// peut compter 5 000 lignes, on ne garde en mémoire que celles qui viennent d'être affichées. Les valeurs retirées du cache
/// (trop anciennes, remplacées, supprimées ou vidées) sont libérées (<see cref="IDisposable.Dispose"/>) si elles le permettent.
/// Classe pure (aucune dépendance à Windows Forms ni à GDI+) : testable avec de fausses images. Utilisable depuis un seul thread
/// à la fois (le thread d'interface).
/// </summary>
/// <typeparam name="TKey">Clé (le texte du code-barres).</typeparam>
/// <typeparam name="TValue">Valeur (la miniature).</typeparam>
internal sealed class BarcodeBatchLruCache<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : class
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new(); // en tête : la plus récemment utilisée
    private bool _disposed;

    private readonly struct Entry
    {
        internal Entry(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        internal TKey Key { get; }

        internal TValue Value { get; }
    }

    /// <param name="capacity">Nombre maximal d'éléments gardés (au moins 1).</param>
    /// <param name="comparer">Comparaison des clés (null : comparaison par défaut).</param>
    internal BarcodeBatchLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "La capacité doit être d'au moins 1.");
        }

        Capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    /// <summary>Nombre maximal d'éléments gardés.</summary>
    internal int Capacity { get; }

    /// <summary>Nombre d'éléments actuellement gardés.</summary>
    internal int Count => _map.Count;

    /// <summary>Nombre de recherches qui ont trouvé leur élément.</summary>
    internal long Hits { get; private set; }

    /// <summary>Nombre de recherches sans résultat.</summary>
    internal long Misses { get; private set; }

    /// <summary>Nombre d'éléments retirés parce que la capacité était atteinte.</summary>
    internal long Evictions { get; private set; }

    /// <summary>Vrai si la clé est présente (sans la rendre « récente » ni compter une recherche).</summary>
    internal bool Contains(TKey key) => _map.ContainsKey(key);

    /// <summary>Clés dans l'ordre, de la plus récemment utilisée à la plus ancienne (pour les tests).</summary>
    internal IReadOnlyList<TKey> KeysByRecency()
    {
        var keys = new List<TKey>(_map.Count);
        foreach (Entry entry in _order)
        {
            keys.Add(entry.Key);
        }

        return keys;
    }

    /// <summary>Cherche un élément ; s'il existe, il devient le plus récent.</summary>
    internal bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            Hits++;
            Promote(node);
            value = node.Value.Value;
            return true;
        }

        Misses++;
        value = default!;
        return false;
    }

    /// <summary>
    /// Rend l'élément de la clé, en le fabriquant avec <paramref name="factory"/> s'il est absent. Si la fabrication échoue,
    /// l'exception remonte et rien n'est mis en cache. L'élément rendu n'est jamais retiré par cet appel même.
    /// </summary>
    internal TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGet(key, out TValue existing))
        {
            return existing;
        }

        TValue created = factory(key);
        Add(key, created);
        return created;
    }

    /// <summary>Ajoute (ou remplace) un élément, qui devient le plus récent ; retire les plus anciens au-delà de la capacité.</summary>
    internal void Add(TKey key, TValue value)
    {
        if (_disposed)
        {
            DisposeValue(value);
            return;
        }

        if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            TValue old = node.Value.Value;
            _order.Remove(node);
            _map.Remove(key);
            if (!ReferenceEquals(old, value))
            {
                DisposeValue(old);
            }
        }

        var added = new LinkedListNode<Entry>(new Entry(key, value));
        _order.AddFirst(added);
        _map[key] = added;

        while (_map.Count > Capacity && _order.Last != null)
        {
            LinkedListNode<Entry> oldest = _order.Last;
            _order.RemoveLast();
            _map.Remove(oldest.Value.Key);
            Evictions++;
            DisposeValue(oldest.Value.Value);
        }
    }

    /// <summary>Retire (et libère) un élément. Vrai s'il existait.</summary>
    internal bool Remove(TKey key)
    {
        if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            return false;
        }

        _order.Remove(node);
        _map.Remove(key);
        DisposeValue(node.Value.Value);
        return true;
    }

    /// <summary>Vide le cache en libérant tous les éléments.</summary>
    internal void Clear()
    {
        var values = new List<TValue>(_map.Count);
        foreach (Entry entry in _order)
        {
            values.Add(entry.Value);
        }

        _order.Clear();
        _map.Clear();
        foreach (TValue value in values)
        {
            DisposeValue(value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
    }

    private void Promote(LinkedListNode<Entry> node)
    {
        if (!ReferenceEquals(_order.First, node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    private static void DisposeValue(TValue value)
    {
        try
        {
            (value as IDisposable)?.Dispose();
        }
        catch
        {
            // Une libération qui échoue ne doit jamais gêner l'affichage.
        }
    }
}
