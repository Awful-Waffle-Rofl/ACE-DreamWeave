/* Retire the four per-ability affinity rate keys Soul Jump and Void Damage used to carry:
   class_ability_affinity_souljump_rate_per_trained, _per_spec, and
   class_ability_affinity_voiddamage_rate_per_trained, _per_spec.

   2026-10-02 OWNER RULING: rather than standardizing these four keys' VALUES onto the shared
   class_ability_affinity_rate_per_trained / _per_spec pair (which would need a second migration the
   next time the shared pair itself changes), both abilities now call the single-argument
   GetClassAbilityAffinityMultiplier(Skill) overload and read the shared pair directly. The four
   per-ability keys have no reader left and are retired outright, not merely re-pointed.

   A HAND-TUNED VALUE IS DISCARDED HERE, DELIBERATELY. Unlike a guarded default-forwarding migration,
   this DELETE is unconditional on value - a retired key has no reader, so an operator-tuned row is as
   dead as a default one (same shape as 2026-09-26-02-Retire-Account-Vault-Class-Fold-Budget.sql and
   2026-09-22-02-MlDigsiteRetireBroadcastRadius.sql). Both abilities now move with the shared pair
   instead, which is the point of retiring rather than restating.

   RE-RUNNING IS A NO-OP: once a row is gone the WHERE clause matches nothing, and a shard that never
   had the key matches nothing either. */

DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_affinity_souljump_rate_per_trained';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_affinity_souljump_rate_per_spec';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_affinity_voiddamage_rate_per_trained';
DELETE FROM `config_properties_double` WHERE `key` = 'class_ability_affinity_voiddamage_rate_per_spec';
