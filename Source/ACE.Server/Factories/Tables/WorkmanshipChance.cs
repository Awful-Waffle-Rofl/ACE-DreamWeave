using System;
using System.Collections.Generic;

using ACE.Server.Factories.Entity;

namespace ACE.Server.Factories.Tables
{
    public static class WorkmanshipChance
    {
        private static ChanceTable<int> T1_Chances = new ChanceTable<int>()
        {
            ( 1, 0.05f ),
            ( 2, 0.15f ),
            ( 3, 0.3f ),
            ( 4, 0.3f ),
            ( 5, 0.2f ),
        };

        private static ChanceTable<int> T2_Chances = new ChanceTable<int>()
        {
            ( 2, 0.05f ),
            ( 3, 0.15f ),
            ( 4, 0.3f ),
            ( 5, 0.3f ),
            ( 6, 0.2f ),
        };

        private static ChanceTable<int> T3_Chances = new ChanceTable<int>()
        {
            ( 3, 0.05f ),
            ( 4, 0.15f ),
            ( 5, 0.3f ),
            ( 6, 0.3f ),
            ( 7, 0.2f ),
        };

        private static ChanceTable<int> T4_Chances = new ChanceTable<int>()
        {
            ( 3, 0.01f ),
            ( 4, 0.05f ),
            ( 5, 0.15f ),
            ( 6, 0.3f ),
            ( 7, 0.29f ),
            ( 8, 0.2f ),
        };

        private static ChanceTable<int> T5_Chances = new ChanceTable<int>()
        {
            ( 3, 0.01f ),
            ( 4, 0.03f ),
            ( 5, 0.05f ),
            ( 6, 0.25f ),
            ( 7, 0.46f ),
            ( 8, 0.15f ),
            ( 9, 0.05f ),
        };

        private static ChanceTable<int> T6_Chances = new ChanceTable<int>()
        {
            ( 4, 0.01f ),
            ( 5, 0.04f ),
            ( 6, 0.25f ),
            ( 7, 0.25f ),
            ( 8, 0.25f ),
            ( 9, 0.15f ),
            ( 10, 0.05f ),
        };

        // T7 and T8 continue the existing curve rather than extending it linearly. The lower tiers step
        // their mean up by a full point each (T1 3.45 -> T2 4.45 -> T3 5.45), then decelerate as they
        // approach the hard ceiling of 10 (T4 6.41 -> T5 6.77 -> T6 7.34). These carry that deceleration
        // on: T7 7.94, T8 8.44.
        //
        // The number that actually matters for balance is not the mean, it is P(workmanship 10) - the
        // Equipment/WeaponMod systems scale magnitude linearly by workmanship / 10, so players only ever
        // mod a 10. Under T6 that stayed pinned at 5% across every tier from 6 up, which made the search
        // cost for a moddable endgame item flat over the whole top half of the game. 8% at T7 and 15% at
        // T8 is a deliberate ~3x easing at the top, chosen over a larger step so it can be raised later
        // without recalling drops players already hold.
        private static ChanceTable<int> T7_Chances = new ChanceTable<int>()
        {
            ( 5, 0.01f ),
            ( 6, 0.05f ),
            ( 7, 0.3f ),
            ( 8, 0.35f ),
            ( 9, 0.21f ),
            ( 10, 0.08f ),
        };

        private static ChanceTable<int> T8_Chances = new ChanceTable<int>()
        {
            ( 6, 0.02f ),
            ( 7, 0.15f ),
            ( 8, 0.35f ),
            ( 9, 0.33f ),
            ( 10, 0.15f ),
        };

        private static readonly List<ChanceTable<int>> workmanshipChances = new List<ChanceTable<int>>()
        {
            T1_Chances,
            T2_Chances,
            T3_Chances,
            T4_Chances,
            T5_Chances,
            T6_Chances,
            T7_Chances,
            T8_Chances,
        };

        /// <summary>
        /// Rolls for a 1-10 workmanship for an item
        /// </summary>
        public static int Roll(int tier)
        {
            // tiers only run 1-8; clamped rather than indexed blind, to match MutateValue_Generic
            tier = Math.Clamp(tier, 1, 8);

            var workmanshipChance = workmanshipChances[tier - 1];

            return workmanshipChance.Roll();
        }

        /// <summary>
        /// Returns the workmanship modifier for an item
        /// </summary>
        public static float GetModifier(int? workmanship)
        {
            var modifier = 1.0f;

            if (workmanship != null)
                modifier += workmanship.Value / 9.0f;

            return modifier;
        }
    }
}
