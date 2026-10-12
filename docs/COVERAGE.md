# Матрица покрытия игровых механик (кооп-мод)

Дата анализа: 2026-10-12. Метод: статический разбор `decompiled/Assembly-CSharp/` (1086 файлов, 1135 классов) и `src/LuckyTowerCoop/`; игру не запускали. `QUEUE-REPORT.md` не существует, поэтому «проверено» взято из `docs/PLAN.md` (раздел «Состояние»), `docs/TASKS.md` и `README.md`.

## Как читать
- **GM / CP** — число вхождений `GameManager.Instance.Player` (также `InstanceNoWarnings`, `?.`) и `Player.CurrentPlayer` в файле класса; для строк с несколькими классами сумма по уникальным файлам. Это вхождения, а не строки (`grep -c` даёт чуть меньше). Класс, вложенный в чужой файл, делит счётчики с файлом. `Player.VonWansts[0]` в игре встречается ещё 41 раз (в основном `CharacterUtilities`, `PatrolingEnemyUtilities`).
- **Покрытие** — какой механизм мода работает:
  - *глушится у клиента* — Update/FixedUpdate/LateUpdate, триггеры и коллизии класса выключены у клиента (`WorldPatches`: все наследники `Trap`, кроме `FlyingBehaviour` и `SpecialItem`, плюс `s_extraTypes`; `NetPatches`: Player, Enemy, NPC, FlyingBehaviour);
  - *подмена P2* — на время вызова «текущий игрок» = P2 (`TargetSwap`): корутины P2 и унаследованные от них, физ. колбэки при входе коллайдера P2, Update компонентов на объекте P2, `GetDamaged`, `TransformAction`, удар оружием, кошель; для ИИ — ближайший игрок (`Enemy`, злой `NPC`, `FlyingBehaviour`, `EnemyDetectionRange`);
  - *пересылается* — сообщения `Window`, `Talk`, `LightsOut`, `PlayerState`, `SceneLoad` и снапшоты объектов из `NetHost.WorldObjects` (всё с `Animator`, `Rigidbody2D`, `Trap`, `Lever`, `Damagable`, `TreasureChest`, `FlyingBehaviour`, `Character`, `PickupItem`);
  - *сидировано* — `RandomizerPatches` (16 типов), `CaveSeedPatch`, общий сид забега (`SegmentManager.ResetSeed`);
  - *нет* — специального кода нет.
- **Статус**: ✅ покрыто механизмом и названо проверенным в PLAN/TASKS/README; 🟡 покрыто механизмом, не проверено; 🔴 по коду, вероятно, не работает для P2 или у клиента (причина в 5–10 словах).
- Ключевая особенность: `Update` ловушек и менеджеров на хосте **не** обёрнут подменой. Подмена действует только на объектах P2, в колбэках P2 и в корутинах, запущенных под подменой. Всё, что читает `Player.CurrentPlayer` из чужого `Update`, видит P1.
- README (раздел «Известные ограничения») строкой про зелье превращения устарел: PLAN (фаза 5) называет зелье-монстр для P2 проверенным; про бутылку возрождения строка верна.

**Сводка (105 механик): ✅ 10 · 🟡 67 · 🔴 28.**

| Раздел | ✅ | 🟡 | 🔴 | всего |
|---|---|---|---|---|
| 1. Ловушки | 2 | 18 | 7 | 27 |
| 2. Особые комнаты и испытания | 4 | 16 | 3 | 23 |
| 3. NPC и диалоги | 1 | 7 | 5 | 13 |
| 4. Злой маг Эвелиус | 0 | 1 | 4 | 5 |
| 5. Компаньоны | 0 | 3 | 3 | 6 |
| 6. Особые предметы и зелья | 2 | 10 | 2 | 14 |
| 7. Смерть и воскрешение | 0 | 5 | 0 | 5 |
| 8. Деревня | 0 | 2 | 0 | 2 |
| 9. Концовки и катсцены | 0 | 1 | 2 | 3 |
| 10. Достижения | 0 | 1 | 0 | 1 |
| 11. Общие системы | 1 | 3 | 2 | 6 |

## 1. Ловушки

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Шипы: пол, коннектор, колья | `TrapSpike`, `TrapSpikeConnector`, `StakeTrap` | Шипы выдвигаются по таймеру/триггеру и бьют по слою Player | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 | урон по слою Player: P2 страдает как P1 |
| Стомперы и давилки | `Stomper`, `CrusherTrap`, `CrusherManagerSemi`, `CrusherManager`, `DungeonEndCrusherMisc` | Падающие прессы, давилка в конце подземелья | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 | пыль стомпера не регистрируется (косметика) |
| Наковальня | `Anvil` | Падает при проходе; рвётся об острое оружие | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 |  |
| Стена со стрелами и бьющая стена | `ArrowWall`, `PunchWall` | Стреляет стрелами / бьёт кулаком из стены | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 |  |
| Маятник | `TrapPendulum` | Раскачивающийся маятник | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 |  |
| Подвесная бомба | `HangingBomb` | Бомба на верёвке: срезается и взрывается | 0 / 3 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 | проверка «оружие в руке» в OnTriggerEnter2D идёт под подменой P2 |
| Мост-платформа | `BridgePlatform` | Мост поднимается/опускается по рычагу | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 |  |
| Рушащийся пол и провалы | `TrapCrumblingGap`, `DungeonEndCrumblingStep`, `ParkourGapInFloorTrap`, `TrapGap` | Пол падает под весом, парковые провалы, пасть хватает персонажа | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 | жалоба «пол падает поздно» закрыта фазой 2, повторно вживую не проверялась |
| Монстры из пропастей и стен | `PotentialMonster`, `MonsterInGap`, `MonsterInGapTrigger`, `MonsterInWall`, `MonsterInWallTrigger`, `MonsterInWallGrab` …(+1) | Монстр выскакивает из пропасти/стены при проходе | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2; созданное на ходу создаётся у клиента из префаба | 🟡 | «есть в 0.3.2 — проверить» (PLAN, фаза 2) |
| Крюки | `Hook`, `HookOnTouch`, `HookedItemParent` | Крюк цепляет предмет или персонажа | 1 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 |  |
| Ветровая зона | `TrapWindArea` | Ветер сдувает игрока | 0 / 4 | глушится у клиента; в снапшоте; колбэки под подменой P2, **Update без подмены (цель = P1)** | 🟡 | условие запуска идёт по CurrentPlayer (P1), зона ловит оба коллайдера |
| Молотобоец (TrapHammerGuy) | `TrapHammerGuy`, `Hammerguy`, `TrapHammerguyFallSmallGap` | Погоня молотобойца за игроком и его компаньонами | 0 / 40 | глушится у клиента; в снапшоте; колбэки под подменой P2, **Update без подмены (цель = P1)** | 🔴 | гонится и бьёт только Player.CurrentPlayer (P1) |
| Молотобоец в стене | `HammerGuyWall` | Хватает компаньонов игрока из стены | 4 / 0 | глушится у клиента; в снапшоте; колбэки под подменой P2, **Update без подмены (цель = P1)** | 🔴 | берёт группу компаньонов только P1 |
| Спящий гигант (Sleeper, TrapSleeper) | `Sleeper`, `TrapSleeper` | Стелс: рядом нельзя бегать, прыгать, бить, иначе проснётся | 0 / 30 | глушится у клиента; в снапшоте; колбэки под подменой P2, **Update без подмены (цель = P1)** | 🔴 | будится вводом P1 (Input.GetKey его клавиш) |
| Оркестр (TrapBigBand) | `TrapBigBand` | Музыканты просыпаются, если игрок шумит рядом; барабаны | 0 / 18 | глушится у клиента; в снапшоте; колбэки под подменой P2, **Update без подмены (цель = P1)**; звук мира синхронизируется | 🔴 | стелс-проверка по вводу и позиции P1 |
| Свет погас (TrapLightsOut) | `TrapLightsOut`, `LightsOutLever`, `LightsOutTrigger`, `DoorLightEvents`, `TorchLightHandler` | Темнота в комнате, свет возвращает рычаг или выход | 6 / 9 | глушится у клиента; в снапшоте; колбэки физики под подменой P2; пересылается Msg.LightsOut | ✅ | «свет погас» в обе стороны проверен автотестом (PLAN) |
| Монстр в темноте | `LightsOutMonster` | Монстр появляется в темноте и душит игрока | 1 / 1 | не глушится (MonoBehaviour вне списка); в снапшоте | 🔴 | m_currentPlayer кэширует P1 |
| Бочка с порохом | `TrapPowderKeg`, `SpecialItemPowderKeg`, `KegActivation`, `PowderKegEquipHelper` | Взрыв башни: видео, смерть игрока, конец забега | 13 / 4 | глушится у клиента; в снапшоте; колбэки физики под подменой P2; предмет-версия не глушится | 🔴 | убивает только slot0, видео клиенту не идёт (Task D) |
| Рычаги и головоломки | `Lever`, `PuzzleLever`, `PuzzleLeverManager`, `TriggerCounter`, `PuzzlePortculis`, `PuzzleWeightedStep` …(+2) | Рычаги открывают решётки, шипы, таймер головоломки | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2; удар P2 — оружие под подменой | ✅ | «запертые комнаты с рычагами» — автотест 12.10 |
| Плиты-весы | `WeightedStepParent`, `GamblerWeightedStep`, `ContactSpikesWeightedStep`, `IndianaWeightedStep`, `SpecialWeightTrigger` | Нажимные плиты открывают двери и ловушки | 1 / 1 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 (подклассы со своими методами не в списке) | 🟡 | `m_playerInArea = CurrentPlayer` |
| Летучие мыши и пчёлы | `BatBehaviour`, `BeeHaviour`, `FlyingBehaviour` | Летающие враги-ловушки, следят за игроком | 1 / 0 | глушится у клиента; цель — ближайший игрок (AiTargetPatches) | 🟡 | исправлено падение BatBehaviour.OnDisable у клиента |
| Всасывающая труба | `SuctionPipe` | Труба затягивает игрока и переносит по трубе | 3 / 0 | не глушится у клиента; хост: колбэки под подменой P2 | 🔴 | затягивает только GameManager.Player |
| Слизь на полу | `GooOnFloor`, `GooOnFloorTrigger`, `GooMidAir` | Скользкая слизь: замедление и блок ввода | 7 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 | скорость/ввод меняются у GM.Player вне колбэка P2 |
| Дыры, печь, верёвки, потолочные давилки | `HoleInWall`, `Oven`, `Clothesline`, `CeilingCorpseCrusher`, `AimingTrapBehaviour`, `TrapTrashWindow` …(+2) | Разные механизмы: дыра в стене, печь, бельё, мусорное окно, рука из провала | 0 / 0 | не глушится у клиента; в снапшоте; хост: колбэки под подменой P2 | 🟡 | клиент исполняет логику параллельно хосту; урон у клиента блокирован |
| Кроты, ритм, язык, силомер | `Mole`, `MoleHole`, `MoleHoleTrap`, `MoleTriggers`, `RythmTrap`, `Tongue` …(+2) | Мини-механизмы: кроты, ритм-ловушка, язык, шипы Индианы | 0 / 0 | не глушится у клиента; в снапшоте | 🟡 | Random в OnEnable/Start не сидирован |
| Двери-ловушки | `TrapFakeDoor`, `CardBoardDoor`, `DialogueFakeDoor`, `IronMaidenDoor`, `MonsterDoor`, `ToiletDoor` …(+2) | Поддельные двери: картон, железная дева, туалет, монстр, дверь в гибель | 1 / 1 | BalconyFakeDoor глушится; остальные не глушатся; в снапшоте | 🟡 | «двери-ловушки не видны» закрыто фазой 2/0.3.4 |
| Урон от касания | `DamageOnTouch` | Общий компонент урона: шипы, снаряды, враги | 0 / 5 | клиент: GetDamaged заглушён; хост: по месту | 🟡 | 5 обращений к CurrentPlayer |

## 2. Особые комнаты и испытания

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Генерация: Regular, HUB, Exit, EnemyOnly, SegmentEnd | `RoomGenerator`, `SegmentManager`, `Room` | Сегмент строится целиком по сиду, двери соединяются | 13 / 0 | сидировано (общий сид забега, SegmentSeed); RoomCheck и resync | ✅ | отпечатки `[Gen]` сегментов 1–3 совпали (PLAN 12.10) |
| Unique: Gambler | `GamblerBehavior`, `GamblerDialogue`, `Gambler`, `GamblerAction`, `GamblerUtilities`, `PuzzleMonsterGamble` …(+2) | Игра с монстром в кости, призы, драка при проигрыше | 39 / 2 | подмена P2 (диалог, корутины); Gambler — Enemy (ближайший); окно пересылается | 🟡 | 36 GM.Player в DialogueLogic: верно, только пока цепочка под подменой P2 |
| Unique: DoorToDoom | `TrapDoorToDoom` | Дверь в «гибель»: особая ветка в GameManager.EnterDoorRoutine | 0 / 0 | не глушится; переход — голосование дверей | 🟡 | ветки DoorToDoom (GameManager :2163, :2331, :2477) не проверены |
| Unique: IndianaJones | `IndianaJonesRoomGenerator`, `IndianaJonesSpikes`, `IndianaWeightedStep` | Комната с идолом: плита-весы, шипы, обвал | 0 / 0 | плита глушится; генератор использует Random без сида | 🟡 | IndianaJonesRoomGenerator не в списке сидирования |
| Unique: HiddenTreasure | `HiddenRoom`, `SpawnTreasure`, `FillChest`, `LockedItems` | Скрытая комната с сокровищем | 1 / 0 | монеты приходят объектами хоста; SpawnTreasure не сидирован | 🟡 |  |
| Unique: SantaRoom | `SnowyPufflesDoors`, `SnowyPufflesHelper` | Комната Санты с дверями-пуффлами | 4 / 0 | не глушится; сегмент берёт GM.Player.Stats.SegmentID | 🟡 |  |
| Unique: BonusRoom, BonusRoomNoCompanion | `BonusRoomDoor` | Бонусная комната через особую дверь | 2 / 0 | сидировано; голосование дверей | ✅ | бонус-комнаты проверены автотестом 12.10 |
| Companion, CompanionHUB | `CompanionSpawnSpot`, `CompanionAutomat`, `CompanionRandomizer`, `CompanionBalconyTrap` | Комната автомата компаньонов | 4 / 0 | CompanionRandomizer сидирован; CompanionBalconyTrap глушится; оплата под подменой P2 | 🟡 | оплата `GM.Player.Stats.CurrentGoldCount`; см. раздел «Компаньоны» |
| Cauldron: котёл-головоломка | `PuzzleCauldron`, `PuzzleMakePotion`, `CauldronPullLever`, `CauldronPump`, `IngredientSlot`, `PuzzlePotion` | Варка зелья из ингредиентов, рычаг, насос | 0 / 5 | рычаги глушатся; IngredientSlot без сида | 🟡 |  |
| LockedChest, ChestKey | `TreasureChest`, `TreasureKey`, `ChestSpawner`, `OpenChestHint` | Сундуки, ключи, запертые сундуки | 0 / 8 | сундук и ChestSpawner глушатся; ChestSpawner сидирован; despawn пересылается | 🟡 | «сундук не разбить» исправлен фазой 2, не помечен проверенным |
| Crusher, Crumbling (конец подземелья) | `CrusherManager`, `DungeonEndCrumblingStep`, `DungeonEndCrusherMisc` | Финальная давилка и рушащиеся ступени | 0 / 0 | глушится у клиента; в снапшоте; колбэки физики под подменой P2 | 🟡 |  |
| Torture: железная дева | `IronMaidenDoor`, `TrapFakeDoor` | Комната пыток: дверь-железная дева | 0 / 0 | не глушится; в снапшоте | 🟡 |  |
| Lights out (комната) | `TrapLightsOut`, `LightsOutTrigger`, `LightsOutLever`, `LightsOutMonster` | Темнота, рычаг света, монстр | 7 / 10 | глушится; Msg.LightsOut | ✅ | свет проверен; монстр — отдельная 🔴 в разделе ловушек |
| Balcony, BalconyEntrance, BalconyExit | `BalconyGenerator`, `BalconyBird`, `BalconyFakeDoor`, `BalconyDropTrigger`, `BalconyHatch`, `BalconyWindManager` …(+3) | Внешняя стена: окна, птицы, ловушки-двери, ветер, люк | 4 / 3 | глушится (птица, дверь, зона сброса); сидировано (генератор, ветер); в снапшоте | ✅ | 458/458 объектов (PLAN 12.10); слои глубины общие (README) |
| LabHUB, LabRegular, LabKey, LabExit | `LabHubManager`, `LabAlarm`, `PatrolingEnemy`, `PatrolingEnemyUtilities`, `LabEnemyStates` | Лаборатория: многослойный хаб, патрули, тревога по слоям | 0 / 29 | ИИ по ближайшему (Enemy.Update); слой глубины общий | 🔴 | слой static CurrentPlayerLayer один на двоих |
| Cave, CaveUnique | `CaveMadnessTrigger`, `CaveDoorSwitcher`, `MonsterCaveSpot`, `CaveConfiguration`, `MonsterSpawnSpot` | Пещеры после башни: безумие, монстры, двери | 2 / 0 | сидировано (CaveSeedPatch) | 🟡 | «ждёт проверки» (PLAN); задача очереди E |
| SewerEntrance, SewerExit | `DrippingPipe`, `Critters`, `Snail`, `Rat`, `BaseAnimal` | Канализация: декор и мелкие существа; логика — флаги RoomGenerator | 0 / 0 | сидировано (общий сид); декор не регистрируется | 🟡 | отдельных классов канализации нет |
| TowerExit, EndGameSpawn, выход из башни | `EndgameExitroomTrigger`, `ExitAutomatHelper`, `EndgameSettings`, `EveliusBasement` | Выход из башни, спавн финала, сердце Эвелиуса в руках | 6 / 0 | не глушится; сердце ищется у GM.Player | 🔴 | сердце ищется только в руках slot0 |
| Tutorial | `TutorialEvelius`, `CommandEvents`, `IntroHelper`, `SkipIntro` | Обучающая сцена и подсказки | 3 / 0 | нет | 🟡 | не нужна для P2; CommandEvents.m_player = GM.Player |
| ConnectRoomStart, ConnectRoomTarget | `RoomGenerator` | Соединительные комнаты между сегментами | 7 / 0 | сидировано | 🟡 | в генераторе, отдельно не проверялись |
| Head-комнаты: ItemVault, Pawnshop, PillarWalk, BarberShop, HairMirror | `VonWanstHeadManager`, `ItemVault`, `PillarWalk`, `HeadRoom`, `HeadRoomPedestal`, `HeadroomExitDoor` …(+3) | Игрок уходит в «голову Ванста» с копией на ограниченное время | 18 / 9 | нет (одиночный менеджер, отключает slot0) | 🔴 | отключает Player.CurrentPlayer, подменяет GM.Player копией |
| Укрытия (HideOut) | `HideOut`, `HideoutSpecialItem` | Прятаться в бочке/шкафу, компаньоны выходят рядом | 3 / 1 | подмена P2 при триггере | 🟡 |  |
| Зеркальная платформа | `MirrorPlatform` | Платформа-зеркало работает при входе игрока | 0 / 0 | подмена P2 (физ. колбэки) | 🟡 | «зеркало только для хоста» — фаза 3, не помечено проверенным |

## 3. NPC и диалоги

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| База диалогов: DialogueTrigger + DialogueManager | `DialogueTrigger`, `DialogueManager`, `TalkCommand`, `FirstOptionCommand`, `SecondOptionCommand`, `ThirdOptionCommand` | Триггер NPC ставит себя в очередь менеджера и открывает меню | 15 / 14 | подмена P2 (колбэки, Update менеджера P2); Talk пересылается | 🔴 | триггер кэширует DialogueManager P1 (Awake/Start) |
| Окно выбора (InteractionWindow) | `InteractionWindow` | Меню 1/2/3: диалоги, магазины, джинн, банк | 1 / 12 | пересылается (Msg.Window); локальное открытие у клиента глушится | ✅ | открытие и закрытие у клиента — автотест 12.10 |
| Реплики: субтитры и голос | `CharacterAudioComponents`, `TextboxPositionHandler`, `ReparentTextbox` | Реплики над головой, озвучка | 2 / 0 | пересылается (Msg.Talk); клиент свои не запускает | 🟡 | DialogueManager.Initialize у P2 вызывается при GM.Player = P1 |
| Автоматы: Automat, AutomatenManager | `Automat`, `AutomatenManager`, `AutomatWindowAnimation`, `PriceTagValue` | Торговый автомат: рычаг, цена, выдача предмета | 3 / 0 | рычаг глушится у клиента; удар P2 под подменой; товар и цена не сидированы | 🟡 | Task A (оплата P2) в очереди; клиент может видеть другой товар |
| Ломбард (Pawnshop) | `Pawnshop`, `PawnshopMain`, `PawnshopSell`, `PawnshopDisplayItems`, `Pawnshopevents`, `PawnshopBaseItems` | Продажа и выкуп предметов, оценка, оплата монетами | 36 / 0 | подмена P2 в триггере/окне; кэш менеджера как у базы | 🔴 | меню и оплата могут уйти в окно и кошель P1 |
| Парикмахер (Barber) | `Barber` | Стрижка и борода за монеты, купон | 27 / 0 | подмена P2 в триггере/окне | 🔴 | причёска и оплата берутся у GM.Player |
| Трубочист: Pipefitter, PipeSystem | `Pipefitter`, `PipeSystem`, `PipeSystemManager` | Платная доставка предмета по трубной сети | 21 / 0 | подмена P2; PipeSystem.m_player = GM.Player | 🔴 | оплата и кэш игрока у P1 |
| Банкир, банкомат, монеты | `Banker`, `ATMLogic`, `MoneyTrigger`, `DropAllCoinsDialogue` | Вклад в банк, пузырь монет, бросок всех монет | 14 / 14 | подмена P2; MoneyTrigger показывает кошель GM.Player | 🟡 | пузырь и текст монет — по P1 |
| Мэр, бармен, жители деревни | `Mayor`, `Barkeeper`, `Bard`, `AlvaVillage`, `SobuVillage`, `HubertsMom` …(+8) | Деревенские NPC: налог, питьё, музыка, квесты, татуировки | 108 / 0 | подмена P2; риск базы диалогов | 🟡 | до 27 GM.Player на класс; прогресс села — по P1 |
| Загадочник (RiddleMaster) | `RiddleMaster`, `RiddleMasterAction`, `RiddleMasterUtilities`, `RiddleDatabase` | Загадки с призом | 9 / 1 | подмена P2; вопросы Random | 🟡 |  |
| Зеркало-собеседник (MirrorTalk) | `MirrorTalk` | Реплика зеркала, вход в head-комнату зеркала | 7 / 0 | подмена P2 | 🟡 | ведёт в head-комнату (см. раздел 2) |
| Джинн и лампа | `DjinnData`, `MagicLampSpecialItem` | Лампа вызывает джинна: желания, обезьяна, хранилище | 16 / 0 | подмена P2; TransformAction под подменой | 🟡 | «джинн для второго» — фазы 3/5, вживую не проверен |
| Хижина и сейв предметов | `PlayerHutItemSave`, `WanstHouseHandler`, `HouseUpgradeHelper`, `ItemListCurator` | Предметы в хижине, улучшения дома | 10 / 0 | сейв клиента отключён; сейв хоста — по GM.Player | 🔴 | сохраняет инвентарь только P1 |

## 4. Злой маг Эвелиус

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| EveliusController | `EveliusController` | Эвелиус в хабах: появление, настроение, диалоги, задания | 12 / 6 | глушится у клиента; в снапшоте; лишний у клиента удаляется | 🔴 | m_player = GM.Player: собеседник один |
| Задания, настроение, болтовня | `EvTasks`, `EvReturn`, `EvSmalltalk`, `EvMediumMood`, `EvAnyMood`, `EvGiveItem` …(+2) | Задания, возврат предметов, награда и наказание | 35 / 1 | хост; отдельно не глушится | 🔴 | монеты и вещи списываются у GM.Player |
| EveliusHunter: охота | `EveliusHunter`, `HunterActions`, `HunterMovement`, `HunterCloud`, `HunterAnimationEvents` | Охотник гонится за игроком и принцессой | 21 / 9 | глушится как FlyingBehaviour; цель в Update — ближайший | 🔴 | FixedUpdate, атака, реплики — по CurrentPlayer |
| Сердце Эвелиуса и финал | `EveliusHeartSpecialItem`, `FinalEvelius`, `RuinEvelius`, `EveliusDropArea` | Сердце, финальная башня, руины, бой с Эвелиусом | 37 / 0 | ScenePatches везёт P2 в FinalTower; логики P2 нет | 🔴 | 31 GM.Player; бой и сцена — slot0 |
| Зелье и тело Эвелиуса | `EveliusPotion` | Превращает в Эвелиуса (Status.Evelius) | 0 / 0 | TransformAction под подменой P2 | 🟡 | вживую не проверялось |

## 5. Компаньоны

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Следование и ИИ (NPC + HandleFollow) | `NPC`, `CharacterUtilities` | Идёт за игроком, ждёт, прыгает по слоям | 30 / 107 | клиент: глушится; хост: без подмены (подмена только у злых NPC) | 🔴 | все идут за Player.VonWansts[0] = P1 |
| Разговор и набор в группу | `CompanionDialogueTrigger` | Окно «следуй/стой», подарки, лояльность, освобождение | 16 / 43 | подмена P2 (меню); окно пересылается | 🔴 | группа P2 не та, за кем идёт ИИ (43 CurrentPlayer) |
| Автомат компаньонов | `CompanionAutomat`, `CompanionSpawnSpot` | Покупка компаньона за монеты | 3 / 0 | рычаг глушится; оплата под подменой P2 | 🟡 |  |
| Принцесса | `PrincessAction`, `PrincessUtilities`, `PrincessVillage` | Компаньон-принцесса: поедание, спасение, реплики | 5 / 12 | нет (AI привязан к CurrentPlayer) | 🔴 | 8+4 CurrentPlayer, поведение по slot0 |
| Перенос компаньонов при смене комнат | `EnterDoorCommand`, `CoopTransitions` | Телепорт компаньонов к игроку при переходе | 9 / 2 | CoopTransitions: переход исполняет P1, P2 переносится позже | 🟡 | TeleportCompanionsToPlayer идёт от P1; группа P2 не проверялась |
| Обезвреживание ловушек компаньонами | `UseTrapSpot`, `MultiDismantle`, `CompanionBalconyTrap` | Компаньон обезвреживает ловушку по метке | 1 / 0 | UseTrapSpot и MultiDismantle глушатся у клиента | 🟡 | ловушка спутника только у хоста (0.3.4) |

## 6. Особые предметы и зелья

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Монеты и кошель | `CoinSpecialItem`, `GoldSack`, `GoldSackSpecialItem`, `GoldStash`, `FillGoldSack` | Кошель на 20 монет, подбор, бросок | 12 / 38 | подмена P2 (GoldSack*, Coin Equip) | 🟡 | Task A/B очереди: оплата и передача монет |
| Броня, шлем, щит, манекен | `ArmorSpecialItem`, `HelmetSpecialItem`, `ShieldSpecialItem`, `MannequinSpecialItem`, `HatStandSpecialItem`, `HaircutBustSpecialItem` | Надеваемые предметы | 0 / 0 | подмена P2 (Update предмета на объекте P2) | 🟡 |  |
| Улей-шлем | `HiveSpecialItem` | Пчёлы жалят носителя | 5 / 2 | подмена P2 при ношении | 🔴 | Awake: m_character = CurrentPlayer (P1) |
| Банановая кожура | `TrapItemBananaPeel`, `NutritiousSpecialItem` | Скользкая кожура, скольжение в корутине | 9 / 2 | подмена P2 (корутины) | ✅ | банан на втором заканчивается — проверено (PLAN, фаза 3) |
| Капканы, мышеловка, грабли, гусь | `TrapItemBearTrap`, `TrapItemMouseTrap`, `TrapItemRake`, `TrapItemHonk` | Предметы-ловушки | 0 / 2 | подмена P2 (колбэки) | 🟡 | `hitFrom == null → CurrentPlayer` |
| Взрывоопасное растение | `TrapItemExplosivePlant` | Взрыв по расстоянию до игрока | 4 / 1 | подмена P2 | 🟡 | радиус считается от CurrentPlayer |
| Палочки и заклинания | `MagicWandSpecialItem`, `MagicDevWandSpecialItem`, `RangedSpecialItem`, `SpellSpecialItem`, `SpellHelper` | Заклинания, дальняя атака | 0 / 12 | подмена P2 (предмет в руке) | 🟡 | 12 CurrentPlayer на палочку |
| Песочные часы | `HourglassSpecialitem`, `HeadroomHourglass` | Часы head-комнаты | 4 / 0 | нет | 🟡 | привязаны к head-комнате |
| Бутылка возрождения | `RespawnBottleSpecialItem`, `RespawnSettings`, `RespawnBottleBurner` | Возрождает на месте бутылки (RespawnSettings глобальный) | 1 / 1 | нет (чинится только слот при OnEnable) | 🔴 | RespawnRoutine пересоздаёт slot0 (CODE_MAP) |
| Зубы, молот, липкие, твёрдая еда | `ToothSpecialItem`, `Tooth`, `HammerSpecialItem`, `StickySpecialItem`, `HardFoodSpecialItem` | Зубы-«жизни», молоток, липкий предмет, еда | 0 / 0 | подмена P2 | 🟡 |  |
| Читаемые предметы, пророчества | `ReadableSpecialItem`, `ProphecyScrollSpecialItem`, `ProphecyLibrary`, `ReadProphecyHint` | Свитки и пророчества | 1 / 18 | подмена P2 | 🟡 |  |
| Зелья-головоломки | `PuzzlePotion`, `AntiPotion`, `DamagePotion`, `DeathPotion`, `GooPotion`, `LockengoldPotion` | Бросаемые зелья: урон, слизь, золото, противоядие | 0 / 4 | подмена P2 (бросок из руки) | 🟡 | луч броска берёт localScale CurrentPlayer |
| Превращение: зелье-монстр | `TransformationPotion`, `TransformTo` | Тело игрока заменяется монстром | 0 / 0 | TransformAction под подменой + AdoptAsSecond; клиент создаёт марионетку | ✅ | «монстр, клиент им ходит» — проверено (PLAN, фаза 5) |
| Превращения: джинн (обезьяна), прислужник | `VonWanstHeadManager`, `CharacterAction` | Обезьяна или слуга вместо игрока | 66 / 112 | TransformAction под подменой P2 | 🟡 | фаза 5, вживую не проверены |

## 7. Смерть и воскрешение

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Смерть P2 при живом P1 | `CharacterAction`, `CharacterStats` | Забег продолжается, труп остаётся | 62 / 110 | подмена GetDamaged/Die; DeathPatches | 🟡 |  |
| Смерть P1 при живом P2 | `GameManager`, `CoopPlayers` | P2 становится главным (PromoteSecondToMain) | 0 / 28 | CoopPlayers.PromoteSecondToMain; камера и туман | 🟡 |  |
| Смерть обоих, Game over, похороны | `FuneralEndscreen`, `VillageManager`, `UIManager` | Конец забега, экран смерти | 13 / 0 | ShowDeathUI/Funeral глушатся при живом напарнике; клиент — GameOver по сети | 🟡 |  |
| Воскрешение (мод): убийства или новый этаж | `CoopLife` | Погибший воскресает после N убийств или на новом этаже | 0 / 0 | мод; счётчик на хосте | 🟡 | в PLAN проверенным не назван |
| Камера, туман, свет клиента после смерти | `CameraManager`, `UberFogSystem`, `CoopCamera` | Камера и свет следуют за выжившим | 17 / 1 | SetCurrent: «игрок» = напарник | 🟡 | фаза 4: сделано, не проверено |

## 8. Деревня

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Сцены: башня, деревня, финал | `SceneLoader`, `VillageManager`, `GameManager` | Переход по двери, загрузка сцены, перенос игроков | 0 / 28 | ScenePatches: DontDestroyOnLoad(P2); SceneLoad пересылается | 🟡 | сейв клиента отключён |
| Налоги, сейвы села, дом | `VillageData`, `DataManager`, `HouseUpgradeHelper`, `WanstHouseHandler` | Прогресс деревни, дом, налоги мэра | 0 / 0 | сейв клиента отключён; хост — по игре | 🟡 | инвентарь в сейве только P1 |

## 9. Концовки и катсцены

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Концовки: трон, пещера монстра, бар, похороны | `ThroneEndscreen`, `FuneralEndscreen`, `MonsterCave`, `Barkeeper`, `HubertThrone` | Скриптовые концовки, двигают игрока | 28 / 0 | клиент получает SceneLoad/GameOver; своей логики нет | 🔴 | сцена двигает только GM.Player, P2 вне сценария |
| Видео: взрыв башни, пузырь-переход | `VideoLibrary`, `CameraManager` | Видео на камере: взрыв башни, пузырьковый переход | 9 / 1 | нет (Task D в очереди) | 🔴 | видео играет только хост |
| Интро и заставка | `IntroHelper`, `SkipIntro`, `IdleScreen` | Вступительные ролики, режим ожидания | 1 / 0 | нет | 🟡 | вне забега |

## 10. Достижения

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| AchievementManager и 102 трекера | `AchievementManager`, `AchievementData`, `EnemyDeathDummyAchievementTracker`, `BarenudleBeastAchievementTracker`, `HardyPartyAchievementTracker` | Убийства, смерти, покупки, превращения, побег | 7 / 1 | хук TrackDeath (счётчик убийств); сейв у клиента заглушён | 🟡 | учёт по CharacterTypes.Player; TrackEscape берёт GM.Player |

## 11. Общие системы

| Механика | Классы | Что делает | GM / CP | Покрытие модом | Ст. | Примечание |
|---|---|---|---|---|---|---|
| Двери, люк в сегмент, боковой проход | `EnterDoorCommand`, `EnterDoorTrigger`, `EnterDoorSideTrigger`, `StartNewSegmentTrigger`, `Door` | Переход: голосование, телепорт, камера | 9 / 2 | подмена P2 (голосование CoopTransitions); у клиента заглушено | ✅ | route-test проходит комнаты автоматически (TASKS) |
| Слои глубины (LayerManager, DepthSwitch) | `LayerManager`, `DepthSwitch`, `ObjectLayerSet` | Слои глубины (балкон, лаборатория), общий CurrentPlayerLayer | 30 / 3 | нет | 🔴 | P2 на переключателе меняет слой и P1 |
| Бой (CombatManager, детектор врагов) | `CombatManager`, `EnemyDetectionRange`, `Enemy` | Список врагов в бою, детекторы, цели ИИ | 10 / 35 | подмена: Enemy.Update → ближайший; детектор — владелец | 🟡 | CombatManager.InCombat общий: блокирует разговоры у обоих |
| Туман (UberFog) | `UberFogSystem`, `UberFogPlayerOccluder`, `UberFogHallwayOccluder` | Туман в комнатах, прозрачность вокруг игрока | 12 / 16 | нет | 🔴 | окклюдер привязан к P1 |
| Подсказки (HintManager) | `HintObject`, `HintManager`, `HintCollection` | Обучающие подсказки на экране | 5 / 26 | нет | 🟡 | 36 подклассов HintObject (497 CurrentPlayer суммарно) |
| Тени и глаза | `TwoDShadow`, `TwoDShadow_outdoor`, `EyeTrackingRangeLimiter` | Тени и слежение глаз | 0 / 13 | тени не регистрируются | 🟡 | косметика |

## Топ-15 рискованных 🔴 (по важности для игрока)

Порядок: частота встречи в забеге × насколько ломает игру второго. Номера строк — в `decompiled/Assembly-CSharp/`. Проверка «вживую» — через dev-команды из `docs/TASKS.md` (`tp p2 x y`, `tap p2 talk|opt1|attack`, `log N <фильтр>`); здесь они не запускались.

| № | Риск | Почему ломает | Как исправить (одной фразой) | Файлы и методы игры |
|---|---|---|---|---|
| 1 | NPC, магазины и меню для P2: триггер хранит менеджер диалогов P1 | `DialogueTrigger` кэширует `DialogueManager` первого игрока (`Awake`, `Start`), P2 попадает в очередь `OverlappingTriggers` менеджера P1; диалог после синхронного старта продолжает Update менеджера P1 вне подмены (`MustLookAtTrigger`, `CancelThis` по P1). Вероятная общая причина Task A (оплата P2 списывается у P1) | Патч геттера `DialogueTrigger.DialogueManager` (и `Awake`/`Start`): возвращать менеджер того, кто сейчас «игрок» (`GameManager.Player` под подменой), и вести очереди по игрокам; заодно вызвать `DialogueManager.Initialize` у P2 под подменой | `DialogueTrigger.cs` :106, :111–117, :134–160; `DialogueManager.cs` :369–372, :753, :831–900; `Player.cs` :72, :114; `Pawnshop*.cs`, `Barber.cs`, `Pipefitter.cs`, `Banker.cs` |
| 2 | Компаньоны всегда идут за P1 | `NPC.Update` подменяется только для злых NPC; `HandleFollow` берёт `Player.VonWansts[0]` (P1); набранные P2 получают группу P2, но идут к P1 | Префикс `NPC.Update`: подставлять «лидера» (игрок, чья `GetCurrentPlayerGroup()` содержит NPC), для свободных — ближайшего | `NPC.cs` :258–262; `CharacterUtilities.cs` :2676–2780, :3288; `CompanionDialogueTrigger.cs` :339–348, :991–1018 |
| 3 | Бутылка возрождения | `RespawnSettings` глобальный; `Die` планирует `RespawnRoutine`, которая пересоздаёт slot0; мод глушит только `ShowDeathUI` | Если умирает игрок кооп-пары и напарник жив — не взводить `RespawnSettings.Respawn` для этой смерти (оставить воскрешение `CoopLife`) либо спавнить возрождённого как P2 через `CoopPlayers.SpawnP2` | `CharacterAction.cs` :1584–1602, :1755, :1819; `RespawnBottleSpecialItem.cs` :46, :97; `Player.OnEnable` |
| 4 | Молотобоец гонится только за P1 | `TrapHammerGuy` везде читает `Player.CurrentPlayer`; `Update` ловушки на хосте без подмены; `HammerGuyWall` хватает компаньонов только P1 | Обернуть `Update`/`FixedUpdate`/корутины `TrapHammerGuy` и `HammerGuyWall` в `TargetSwap.SwapTo(NearestFor(ловушка))`, как у `Enemy.Update` | `TrapHammerGuy.cs` :237, :303–355, :455, :615–:862; `HammerGuyWall.cs` :133, :244, :290 |
| 5 | Стелс-ловушки (Sleeper, TrapSleeper, TrapBigBand) | Будятся по вводу и позиции P1, включая `Input.GetKey` его клавиш; P2 можно шуметь без реакции | Подмена на ближайшего «шумного» игрока в их `Update` и замена `Input.GetKey(CommandPattern…KeyCode)` на `PlayerInput` владельца паттерна | `Sleeper.cs` :491, :579, :657–735; `TrapSleeper.cs` :461–465; `TrapBigBand.cs` :559–608 |
| 6 | Эвелиус: один собеседник | `EveliusController.m_player` = `GameManager.Player` при появлении; задания и списания монет — у P1; охотник атакует `CurrentPlayer` в `FixedUpdate` | Под подменой на игрока в триггере/ближайшего вести `EveliusController` (`OnTriggerEnter2D`, корутины), `EveliusHunter.FixedUpdate` — на ближайшего | `EveliusController.cs` :706, :756, :843, :1581, :1680, :1932; `EvTasks.cs` :741, :933; `EveliusHunter.cs` :256–300 |
| 7 | Бочка с порохом | `DestroyTowerRoutine` и `SpecialItemPowderKeg` убивают `GameManager.Player` (slot0) и играют видео только у хоста | Новое сообщение `Msg.TowerExplosion` (Task D) и убийство обоих игроков по очереди | `TrapPowderKeg.cs` :317–346; `SpecialItemPowderKeg.cs` :127–164 |
| 8 | Слои глубины общие (лаборатория, балкон) | `LayerManager.CurrentPlayerLayer` — static; переключатель слоя P2 меняет слой P1, камера и ИИ ориентируются на него | Слой на игрока (словарь) либо игнорировать переключатель, если игрок не slot0; камеру `Lab` вести по сумме обоих | `LayerManager.cs` :13, :198–231; `LabHubManager.cs` :68, :152; `LabAlarm.cs` :123; `CharacterUtilities.cs` :2665, :3391 |
| 9 | Туман (UberFog) привязан к P1 | `UberFogPlayerOccluder` и `UberFogSystem` держат один окклюдер на `Player.CurrentPlayer`; у P2 вокруг тела туман сплошной | Второй окклюдер для P2 (клон `Components.UberFogPlayerOccluder`) и регистрация в `UberFogSystem` | `UberFogPlayerOccluder.cs` :86, :136–156, :195–246; `UberFogSystem.cs` :327–357 |
| 10 | Инвентарь и хижина только у P1 | Сейв и перенос между сценами берут `GetPlayerInventory()` у `GameManager.Player`; `PlayerHutItemSave` — тоже | Второй слот инвентаря в `GameData` для P2 либо запись предметов P2 при выходе из башни | `GameManager.cs` :733, :758, :1116; `EnterDoorCommand.cs` :173; `PlayerHutItemSave.cs` |
| 11 | Head-комнаты (хранилище джинна, Эвелиус, ломбард, парикмахер, зеркало) | `VonWanstHeadManager` отключает `Player.CurrentPlayer`, подменяет `GameManager.Player` копией и возвращает; одиночный менеджер | Запретить вход, пока P2 жив, либо заморозить напарника и вести вход/выход под одной подменой с камерой на вошедшего | `VonWanstHeadManager.cs` :264–273, :337, :405–414; `Djinn.cs` :427; `EvSmalltalk.cs` :38–40 |
| 12 | Финал: сердце Эвелиуса и FinalTower | Проверка сердца и загрузка финала идут по `GameManager.Player`; скриптовые концовки двигают только его | Выполнять `EveliusHeartSpecialItem`, `EndgameExitroomTrigger`, `ThroneEndscreen` под подменой на владельца сердца, P2 переносить и замораживать | `EveliusHeartSpecialItem.cs` :224–257, :337–345; `EndgameExitroomTrigger.cs` :74–77; `ThroneEndscreen.cs` :104–119 |
| 13 | Всасывающая труба и трубная сеть | `SuctionPipe` затягивает `GameManager.Player`; `PipeSystem.m_player` кэширует P1 | Запоминать вошедшего персонажа и вести затягивание/оплату под его подменой | `SuctionPipe.cs` :198, :246, :306; `PipeSystem.cs` :133 |
| 14 | Монстр темноты и смерть по `LightsOut` | `LightsOutMonster.m_currentPlayer` кэширует P1; `TrapLightsOut` убивает/замораживает `GameManager.Player` | Жертва — игрок в тёмной комнате/ближайший; вызывать эти методы под подменой жертвы | `LightsOutMonster.cs` :150, :210; `TrapLightsOut.cs` :394–431 |
| 15 | Автоматы: товар и цена не сидированы | `Automat.Initialize` выбирает товар `WeightedElement.GetRandomElement` и скидку без сида; у клиента табличка и цена могут отличаться от выдачи хоста | Добавить `Automat`, `AutomatenManager`, `CompanionAutomat` в `RandomizerPatches.s_types` | `Automat.cs` :264, :286, :423; `AutomatenManager.cs` :100–177; `CompanionAutomat.cs` :136 |

## Наблюдения, влияющие на много строк

1. **Подмена не покрывает чужие `Update`.** Ловушки, менеджеры и AI-компаненты не на объекте P2 исполняются с `CurrentPlayer = P1` (подмена — только `Enemy`, злой `NPC`, `FlyingBehaviour`, `EnemyDetectionRange`). Строки 🔴 «цель = P1» в основном из-за этого.
2. **Кэши игрока.** Игра сохраняет «игрока» в поле в `Awake`/`Start` и меняет его только через `GameManager.UpdatePlayer` (вызывается мод-кодом лишь при повышении P2). Поля: `DialogueTrigger.m_dialogueManager`, `FlyingBehaviour.m_player` (исправлено), `PipeSystem.m_player`, `EveliusController.m_player`, `LightsOutMonster.m_currentPlayer`, `DepthSwitch.m_playerCollider`, `HiveSpecialItem.m_character`, `HintObject.m_currentPlayer`, `CommandEvents.m_player`, `InteractionWindow.m_player`.
3. **DialogueManager P2.** `Player.GameplayInit` → `DialogueManager.Initialize` читает `GameManager.Player`, а `CoopPlayers.SpawnP2` вызывает `GameplayInit` без подмены: `m_character`, аудио и аниматор менеджера P2 указывают на P1. Реплики P2 озвучиваются и анимируются на P1 (проверить по `[Window]`/`Talk` в логе).
4. **Не глушится у клиента, но имеет Update/триггеры:** `Automat`, `CompanionAutomat`, `Pawnshop*`, `PipeSystem`, `DialogueTrigger`-семейство, `LightsOutMonster`, `SuctionPipe`, `HoleInWall`, `Oven`, `Clothesline`, `CeilingCorpseCrusher`, `AimingTrapBehaviour`, `PuzzleDeathTimer`, `Mole`, `RythmTrap`, `TrapFakeDoor`-семейство. Урон у клиента блокирован (`GetDamaged`), но анимации и спавны могут идти дважды.
5. **Рандом без сида в Awake/Start/OnEnable:** `Automat`, `AutomatenManager`, `CompanionAutomat`, `ItemVault`, `HideOut`, `LabHubManager`, `IngredientSlot`, `SpawnTreasure`, `IndianaJonesRoomGenerator`, `Mole`, `MoleHoleTrap`, `PuzzleMonsterGamble`, `MonsterInWall`, `ConveyorBeltTrapProbability`, `TrapsInletProbaility`, `GooOnFloorActiveProbability`, `MonsterInGapActiveProbability`. Часть выравнивает «маска activeSelf + сверка числа объектов», но тип товара, цена и отдельные спрайты — нет.
6. **Подтверждения из документов.** README: «Часть ловушек, подсказок и реплик компаньонов реагирует только на главного»; «Слои глубины общие»; «бутылка возрождения корректно только для главного». PLAN: бочка с порохом (видео), `leftover`-обломки, пещеры и редкий рассинхрон S03 открыты; фаза 1б и 6 не начаты.
7. **Ограничения анализа.** Учтены только вхождения в тексте декомпиляции; обращения через промежуточные переменные (`var p = GameManager.Instance.Player;`) и через `Character`-поля не видны grep. Статус 🔴 выставлен по чтению кода, без запуска игры; для пунктов 1, 2 и 11 стоит подтвердить вживую (P1 далеко от NPC, P2 входит в триггер).
