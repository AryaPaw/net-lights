import csv
import json
import math
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent

def p(values, q):
    return round(sorted(values)[max(0, math.ceil(len(values)*q)-1)], 3) if values else None

rows = []
for path in sorted(ROOT.glob('run-*/samples.csv')):
    with path.open(encoding='utf-8-sig', newline='') as f:
        for row in csv.DictReader(f):
            row['run'] = path.parent.name
            row['success'] = row['success'] == 'True'
            row['wall_s'] = float(row['wall_s'])
            row['total_s'] = float(row['total_s'])
            rows.append(row)

by_target = defaultdict(list)
by_phase = defaultdict(list)
for row in rows:
    by_target[row['name']].append(row)
    by_phase[(row['run'],row['phase'])].append(row)

def summary(rs):
    good = [r['wall_s'] for r in rs if r['success']]
    return dict(n=len(rs), ok=len(good), p50_s=p(good,.5), p95_s=p(good,.95),
                max_s=round(max(good),3) if good else None,
                errors=[dict(name=r['name'], code=r['code'], exit=r['exit_code'],
                             phase=r['phase'], error=r['error']) for r in rs if not r['success']])

result = dict(total=len(rows), targets={n:summary(rs) for n,rs in by_target.items()},
              phases={run+'/'+phase:summary(rs) for (run,phase),rs in by_phase.items()},
              http429=sum(r['code']=='429' for r in rows),
              http403=sum(r['code']=='403' for r in rows))
(ROOT/'aggregate.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
lines = ['# Измерения контрольных адресов', '',
         'Результат короткого испытания из текущей сети. Это не SLA и не доказательство отсутствия блокировок при круглосуточной работе.', '',
         '| Адрес | Успехи / запросы | Медиана, с | P95 успешных, с | Максимум успешных, с |',
         '|---|---:|---:|---:|---:|']
for name, rs in by_target.items():
    s=summary(rs)
    lines.append(f'| `{rs[0]["url"]}` | {s["ok"]}/{s["n"]} | {s["p50_s"]} | {s["p95_s"]} | {s["max_s"]} |')
lines += ['', 'P95 рассчитан методом ближайшего ранга, только по успешным запросам. Ошибки учтены отдельно; малые выборки не характеризуют редкие задержки.', '',
          'Время включает запуск curl. Каждый запрос создает новое соединение; будущий клиент с повторным использованием соединений нужно проверять отдельно.', '',
          '## Фазы', '']
for (run,phase),rs in by_phase.items():
    s=summary(rs)
    lines.append(f'- {run}, {phase}: {s["ok"]}/{s["n"]} успешных.')
lines += ['', '## Ошибки', '']
for row in rows:
    if not row['success']:
        lines.append(f'- {row["utc"]}: {row["name"]}, {row["phase"]}, curl={row["exit_code"]}, HTTP={row["code"]}: {row["error"]}')
(ROOT/'MEASUREMENTS.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='phases'},ensure_ascii=False))
