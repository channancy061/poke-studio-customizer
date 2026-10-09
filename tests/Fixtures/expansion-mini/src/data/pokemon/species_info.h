#include "constants/abilities.h"

// Invented test fixture. Mimics the layout of pokeemerald-expansion; contains no real game data.

#define EVOLUTION(...) (const struct Evolution[]) { __VA_ARGS__, { EVOLUTIONS_END }, }

const struct SpeciesInfo gSpeciesInfo[] =
{
    [SPECIES_NONE] =
    {
        .speciesName = _("??????????"),
        .levelUpLearnset = sNoneLevelUpLearnset,
    },

    #include "species_info/gen_1_families.h"
};
