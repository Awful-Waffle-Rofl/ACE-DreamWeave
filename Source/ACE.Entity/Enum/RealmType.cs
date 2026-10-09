namespace ACE.Entity.Enum
{
    /// <summary>
    /// ACRealms port: the kind of entry in the realm registry.
    /// Ruleset entries exist for forward compatibility with ACRealms content;
    /// rulesets are stubbed in this port and such entries are ignored.
    /// </summary>
    public enum RealmType : ushort
    {
        Undef = 0,
        Realm = 1,
        Ruleset = 2
    }
}
