# Net Lights instructions

## Version proposals

Only propose or change a version when the active task requests a version proposal or release. Prefer patch for small fixes such as tray behavior, copy, or CI; prefer minor for user-visible features. Propose major only when explicitly requested.

If the release version is unspecified, propose an exact number with a brief reason and wait before editing version metadata. An exact version already supplied by the user needs no repeated confirmation. Ordinary fixes, verification, and branch pushes don't require an unsolicited version decision.

## Publication and preserved work

Follow the global Git/publication policy and actual repository verification sources. Authorization persists for the same active task, action, and target unless revoked; a new message doesn't reset it. A branch push alone doesn't authorize version bumps, tags, or releases.

The existing `.cursor/skills/net-lights-ship/SKILL.md` contains project shipping context. Consult it when that workflow applies and verify version-sensitive commands/current CI against the actual files. Preserve unrelated staged/unstaged work, including pre-existing lockfile changes; don't blindly restore files after a build.

## Интерфейс и общие механизмы

- Инерцией колеса управляет `smooth_scroll` в локальном патче `gpui-pre`. Обычные контейнеры и виртуальный список используют этот движок. Не добавляйте покадровое сглаживание или возврат смещения в отдельных страницах; переход по разделам настроек является отдельным программным действием и отменяется ручной прокруткой.
- Виртуализация истории локаций обязательна. При изменении прокрутки проверяйте также тачпад, границы вложенных областей, смену периода, перетаскивание scrollbar, reduced motion и прекращение запросов кадров после остановки.
- Подтверждения действий показываются штатными уведомлениями GPUI Kit поверх окна, без изменения компоновки страницы. Повторное действие обновляет своё уведомление. Ошибки мониторинга и сохранения остаются постоянными предупреждениями и не затираются подтверждением копирования.
- Геометрия графика является общим источником для рисования, подписей, обводки и попадания курсора. Изменение высоты полосы должно обновлять все эти потребители.
- Подбирать статическую компоновку следует на development-сборке в отдельном профиле. Итоговую Release-сборку и соответствующие проверки выполняйте после согласованной серии изменений. Development-снимок не подтверждает производительность Release.
- При адаптации Argus переносите конкретный механизм из указанного коммита с атрибуцией и проверкой потребителей. Само сходство исходников или наличие UI Kit не подтверждает визуальное соответствие. Общие права, публикацию и правила чужих системных функций из его AGENTS.md не переносите.
