// Invented test fixture: the same lists as an older generation had them.

static const struct LevelUpMove sNoneLevelUpLearnset[] = {
    LEVEL_UP_MOVE(1, MOVE_TACKLE),
    LEVEL_UP_END
};

#if P_FAMILY_SPROUTLE
static const struct LevelUpMove sSproutleLevelUpLearnset[] = {
    LEVEL_UP_MOVE( 1, MOVE_TACKLE),
    LEVEL_UP_MOVE( 4, MOVE_GROWL),
    LEVEL_UP_MOVE(10, MOVE_VINE_LASH),
    LEVEL_UP_END
};

static const struct LevelUpMove sBloomtleLevelUpLearnset[] = {
    LEVEL_UP_MOVE( 1, MOVE_TACKLE),
    LEVEL_UP_MOVE( 1, MOVE_GROWL),
    LEVEL_UP_MOVE( 1, MOVE_VINE_LASH),
    LEVEL_UP_MOVE( 1, MOVE_SLEEP_DUST),
    LEVEL_UP_MOVE( 1, MOVE_CHARM),
    LEVEL_UP_MOVE(20, MOVE_SEED_BOMB),
    LEVEL_UP_END
};
#endif //P_FAMILY_SPROUTLE

#if P_FAMILY_EMBERPUP
static const struct LevelUpMove sEmberpupLevelUpLearnset[] = {
    LEVEL_UP_MOVE( 1, MOVE_TACKLE),
    LEVEL_UP_MOVE( 7, MOVE_EMBER_SNAP),
    LEVEL_UP_MOVE(15, MOVE_PROTECT),
    LEVEL_UP_END
};
#endif //P_FAMILY_EMBERPUP
