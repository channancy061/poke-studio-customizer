// Invented test fixture (same names as pokeemerald-expansion's flags).
#define AI_FLAG_CHECK_BAD_MOVE              AI_FLAG(0)  // AI will avoid using moves that are likely to fail.
#define AI_FLAG_TRY_TO_FAINT                AI_FLAG(1)  // AI will prioritize KOing the player's mon if able.
#define AI_FLAG_CHECK_VIABILITY             AI_FLAG(2)  // AI picks the best available move.
#define AI_FLAG_RISKY                       AI_FLAG(4)  // AI behaves more recklessly.
#define AI_FLAG_HP_AWARE                    AI_FLAG(8)  // AI favours effects based on remaining HP.
#define AI_FLAG_SMART_SWITCHING             AI_FLAG(14) // AI switches more cleverly.
#define AI_FLAG_ACE_POKEMON                 AI_FLAG(15) // The last Pokemon is saved until the end.
#define AI_FLAG_OMNISCIENT                  AI_FLAG(16) // AI knows the player's moves, abilities and items.
#define AI_FLAG_PREDICT_SWITCH              AI_FLAG(23) // AI predicts the player's switches.
#define AI_FLAG_PREDICT_MOVE                AI_FLAG(26) // AI predicts the player's move.

#define AI_FLAG_BASIC_TRAINER         (AI_FLAG_CHECK_BAD_MOVE | AI_FLAG_TRY_TO_FAINT | AI_FLAG_CHECK_VIABILITY)
#define AI_FLAG_SMART_TRAINER         (AI_FLAG_BASIC_TRAINER | AI_FLAG_OMNISCIENT | AI_FLAG_SMART_SWITCHING)
#define AI_FLAG_PREDICTION            (AI_FLAG_PREDICT_SWITCH | AI_FLAG_PREDICT_MOVE)

#define AI_FLAG_COUNT                       64
