#define LEVEL_UP_MOVE(lvl, moveLearned) {.move = moveLearned, .level = lvl}
#define LEVEL_UP_END {.move = LEVEL_UP_MOVE_END, .level = 0}

// Invented test fixture.

static const struct LevelUpMove sNoneLevelUpLearnset[] = {
    LEVEL_UP_MOVE(1, MOVE_TACKLE),
    LEVEL_UP_END
};

#if P_FAMILY_SPROUTLE
static const struct LevelUpMove sSproutleLevelUpLearnset[] = {
    LEVEL_UP_MOVE( 1, MOVE_TACKLE),
    LEVEL_UP_MOVE( 1, MOVE_GROWL),
    LEVEL_UP_MOVE( 7, MOVE_VINE_LASH),
    LEVEL_UP_MOVE(13, MOVE_SLEEP_DUST),
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
    LEVEL_UP_MOVE( 4, MOVE_EMBER_SNAP),
    LEVEL_UP_END
};
#endif //P_FAMILY_EMBERPUP
