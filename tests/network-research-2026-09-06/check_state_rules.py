"""Offline acceptance examples for proposed voting, NOT an implemented tray monitor.

Inputs represent fresh results from one confirmation epoch. A success clears older
failure epochs. The future scheduler must enforce that contract and bound freshness.
"""
import json
from pathlib import Path

def classify(results, settled=True):
    good = {infra for infra, result, wave in results if result == 'ok'}
    # HTTP rejection / bad body / certificate errors don't prove a network outage.
    failures = {infra for infra, result, wave in results if result == 'network-fail'} - good
    waves = {wave for infra, result, wave in results if result == 'network-fail'}
    if len(good) >= 2:
        return 'green'
    if good:
        return 'yellow'
    if settled and len(failures) >= 4 and len(waves) >= 2:
        return 'red'
    return 'gray'

CASES = [
    ('healthy', [('a','ok',1),('b','ok',1)], True, 'green'),
    ('one_dead_target', [('a','network-fail',1),('b','ok',1),('c','ok',1)], True, 'green'),
    ('one_shared_cdn_dead', [('fastly','network-fail',1),('fastly','network-fail',2),('google','ok',1),('cloudflare','ok',2)], True, 'green'),
    ('same_cdn_is_not_four_votes', [('fastly','network-fail',1),('fastly','network-fail',2),('fastly','network-fail',2),('fastly','network-fail',2)], True, 'gray'),
    ('one_route_still_works', [('a','network-fail',1),('b','network-fail',2),('c','network-fail',2),('d','ok',2)], True, 'yellow'),
    ('confirmed_total_failure', [('a','network-fail',1),('b','network-fail',1),('c','network-fail',2),('d','network-fail',2)], True, 'red'),
    ('single_wave_not_enough', [('a','network-fail',1),('b','network-fail',1),('c','network-fail',1),('d','network-fail',1)], True, 'gray'),
    ('pending_results_not_red', [('a','network-fail',1),('b','network-fail',1),('c','network-fail',2),('d','network-fail',2)], False, 'gray'),
    ('rate_limits_not_outage', [('a','429',1),('b','429',1),('c','429',2),('d','429',2)], True, 'gray'),
    ('tls_errors_not_proof_of_outage', [('a','tls-error',1),('b','tls-error',1),('c','tls-error',2),('d','tls-error',2)], True, 'gray'),
    ('new_success_wins', [('a','network-fail',1),('b','network-fail',1),('c','network-fail',2),('d','network-fail',2),('a','ok',2)], True, 'yellow'),
    ('no_fresh_data', [], True, 'gray'),
]

if __name__ == '__main__':
    out = []
    for name, results, settled, expected in CASES:
        actual = classify(results, settled)
        out.append(dict(case=name, expected=expected, actual=actual, passed=actual==expected))
    path = Path(__file__).with_name('state-rule-checks.json')
    path.write_text(json.dumps(out, indent=2), encoding='utf-8')
    print(json.dumps(out))
    assert all(r['passed'] for r in out)
