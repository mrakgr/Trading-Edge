**Subject: Stocks aggregates — confirming construction (my measurements vs. the chatbot answer disagree)**

Hi,

I'm building research on top of your US stocks minute and day flat-file aggregates and want my own calculations to match yours exactly. I've reverse-engineered the construction by rebuilding bars from the raw trades flat files and diffing against your published aggregates. A chatbot reply I got conflicts with what I'm measuring, so I'd appreciate a definitive answer from your data team.

All figures below are **AAPL, 2022-01-03**.

**1. Condition 22 (Prior Reference Price) — which aggregate excludes it?**
The chatbot said the **daily** aggregate *keeps* condition 22 while the **minute** aggregate *drops* it. My measurements show the opposite for daily:

- Rebuilt daily volume **including** cond 22 = 104,746,136 → **+2,148,276 vs. your day-aggregate** (102,597,860)
- Rebuilt daily volume **excluding** cond 22 = 102,666,526 → **+68,666 (~0.07%) vs. your day-aggregate**

So your **day** aggregate appears to **exclude** condition 22, not keep it. Could you confirm the actual rule for the day aggregate? (For reference, cond 22 has `update_rules.consolidated.updates_volume = true` in your Conditions endpoint, yet it's clearly excluded from the day-aggregate volume — so the exclusion goes beyond the published `updates_volume` flag.)

**2. Day vs. minute aggregates — different construction.**
Sum of your minute-aggregate `volume` = 95,619,809, well below the day aggregate (102,597,860), so the daily bar isn't a roll-up of the minute bars. Beyond condition 22, the minute product appears to drop additional prints — e.g. the closing-auction cross (condition 8) and certain after-hours TRF/Form-T prints. Can you confirm how the **closing cross** and **extended-hours TRF prints** are handled in the minute aggregate vs. the day aggregate?

**3. Timestamp used for bucketing.**
My per-minute reconstruction matches far better using `sip_timestamp` than `participant_timestamp` (e.g. clean-RTH minute mismatches: ~14 vs. ~207 of 389 minutes). The chatbot said the sources don't specify. Can your data team confirm whether the aggregates bucket trades by **SIP timestamp**?

A pointer to a written methodology page would also be great if one exists. Thanks very much!
