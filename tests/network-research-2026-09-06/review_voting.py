"""Research model of fresh evidence, not a production scheduler.

Deliberately more conservative than the earlier four-failure example.
Never invents evidence for unobserved, expired or suppressed targets.
"""
from dataclasses import dataclass
from itertools import product
import json
from pathlib import Path

@dataclass(frozen=True)
class Result:
    target: str
    sequence: int
    epoch: int
    finished: float
    outcome: str  # ok, network-error, unusable

def http_reachability(tls_verified, final_status, headers_complete):
    """Reachability of HTTPS origin; deliberately NOT health of a page/API."""
    return bool(tls_verified and headers_complete and 200 <= final_status <= 599)

def classify(config, results, now, epoch, ttl):
    latest = {}
    for result in results:
        if result.target not in config or result.epoch != epoch:
            continue
        if result.target not in latest or result.sequence > latest[result.target].sequence:
            latest[result.target] = result
    fresh = {target: r for target, r in latest.items() if 0 <= now-r.finished <= ttl}
    good = {config[target] for target, r in fresh.items() if r.outcome == 'ok'}
    if len(good) >= 2:
        return 'green'
    if good:
        return 'yellow'
    complete = len(fresh) == len(config) and bool(config)
    all_failed = complete and all(r.outcome == 'network-error' for r in fresh.values())
    independent = len(set(config.values())) >= 3
    if all_failed and independent:
        return 'red'
    return 'gray'

def run():
    config = {str(i):str(i) for i in range(7)}
    checks = []
    def check(name, rs, expected, cfg=config, now=100, epoch=1, ttl=20):
        actual = classify(cfg, rs, now, epoch, ttl)
        checks.append(dict(name=name, expected=expected, actual=actual, passed=actual==expected))
        assert actual == expected, checks[-1]
    def make(outcomes, epoch=1, finished=100):
        return [Result(str(i),1,epoch,finished,o) for i,o in enumerate(outcomes)]

    check('six_alive_one_dead', make(['ok']*6+['network-error']), 'green')
    check('two_alive_five_dead', make(['ok']*2+['network-error']*5), 'green')
    check('one_alive_six_dead', make(['ok']+['network-error']*6), 'yellow')
    check('four_dead_three_unchecked', make(['network-error']*4), 'gray')
    check('all_seven_network_failed', make(['network-error']*7), 'red')
    check('all_service_rejections', make(['unusable']*7), 'gray')
    check('expired_green_must_not_stick', make(['ok']*7, finished=0), 'gray')
    check('late_old_epoch_ignored', make(['ok']*7, epoch=0), 'gray')
    check('future_timestamp_ignored', make(['ok']*7, finished=200), 'gray')
    check('old_success_cannot_override_latest_failure', make(['ok']*7)+[
        Result(str(i),2,1,100,'network-error') for i in range(7)], 'red')
    check('expired_latest_must_not_reveal_old_success', make(['ok']*7)+[
        Result(str(i),2,1,0,'network-error') for i in range(7)], 'gray')
    shared = {str(i):'one-cdn' for i in range(7)}
    check('one_cdn_not_independent_green', make(['ok']*7), 'yellow', cfg=shared)
    check('one_cdn_not_independent_red', make(['network-error']*7), 'gray', cfg=shared)
    for code in (200,204,301,302,403,404,405,429,500,503):
        assert http_reachability(True,code,True)
        assert not http_reachability(False,code,True)
        assert not http_reachability(True,code,False)
    assert not http_reachability(True,0,True)
    assert not http_reachability(True,100,True)

    combinations = 0
    # Exhaustive small input space: a live route must always prevent RED;
    # missing/service-error evidence must never be counted as a network failure.
    for outcomes in product(('ok','network-error','unusable','missing'), repeat=7):
        rs = [Result(str(i),1,1,100,o) for i,o in enumerate(outcomes) if o!='missing']
        state = classify(config,rs,100,1,20)
        assert state != 'red' or all(o=='network-error' for o in outcomes)
        if outcomes.count('ok') >= 2:
            assert state == 'green'
        combinations += 1
    # Virtual 72 hours: a permanently dead endpoint cannot drag an otherwise
    # healthy pool red, and stopping all updates eventually removes green.
    for tick in range(0,72*3600,14):
        rs = make(['ok']*6+['network-error'],finished=tick)
        assert classify(config,rs,tick,1,20)=='green'
        assert classify(config,rs,tick+21,1,20)=='gray'
    output = dict(named_checks=checks, combinations=combinations,
                  http_semantics_checks=32,
                  virtual_hours=72, passed=True,
                  scope='Evidence reducer only; no real outage, timers, rate limiter or tray implementation tested')
    Path(__file__).with_name('review-voting-results.json').write_text(json.dumps(output,indent=2),encoding='utf-8')
    print(json.dumps(output))

if __name__=='__main__':
    run()
