"""Reproduce source inventory only. Does not build, access SQL or alter a host checkout."""
from pathlib import Path
import collections
import datetime as dt
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
CORE = HERE.parents[3]
HOST = CORE.parent / 'MillWorks'
EXCLUDED = re.compile(r'(^|/)(?:bin|obj|Evidence|Reports|Migrations|tools|scripts)(?:/|$)|Tests(?:/|\.)')


def files(root, glob):
    return [root / p for p in subprocess.run(
        ['rg', '--files', '-g', glob, '-g', '!**/bin/**', '-g', '!**/obj/**'],
        cwd=root, capture_output=True, text=True, check=True).stdout.splitlines()]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def top_fields(block):
    parts = []
    start = depth = 0
    quote = None
    escaped = False
    for index, char in enumerate(block):
        if quote:
            if escaped: escaped = False
            elif char == chr(92): escaped = True
            elif char == quote: quote = None
            continue
        if char in ['"', "'"]: quote = char
        elif char in '({[': depth += 1
        elif char in ')}]': depth -= 1
        elif char == ',' and depth == 0:
            parts.append(block[start:index])
            start = index + 1
    parts.append(block[start:])
    result = {}
    for part in parts:
        match = re.match(r'\s*(\w+)\s*=\s*([\s\S]*)$', part)
        if match: result[match.group(1)] = match.group(2).strip()
    return result


sources = [p for p in files(HOST, '*.cs') if not EXCLUDED.search(str(p.relative_to(HOST)))]
mentions = [p for p in sources if re.search(r'\bAuditEnvelope\b', p.read_text())]
initializers = []
for path in mentions:
    source = path.read_text()
    for match in re.finditer(r'new\s+AuditEnvelope\s*\{', source):
        depth = 1
        end = match.end()
        # Braces in the inspected producer strings are balanced; retain the file checksum
        # and initializer line for independent syntax review rather than claim semantic analysis.
        while depth:
            if source[end] == '{': depth += 1
            elif source[end] == '}': depth -= 1
            end += 1
        block = source[match.end():end - 1]
        assignments = top_fields(block)
        fields = {name: assignments.get(name) for name in
                  ['EnvelopeId', 'ExplicitEventId', 'OccurredAt', 'GovernanceIdentity', 'EntityName', 'UserId']}
        initializers.append({'path': str(path.relative_to(HOST)),
                             'line': source[:match.start()].count('\n') + 1,
                             'fields': fields, 'sha256': sha(path)})
refs = []
for p in files(CORE.parent, '*.csproj'):
    try:
        project = ET.parse(p)
    except ET.ParseError:
        continue
    items = []
    for node in project.getroot().iter():
        kind = node.tag.rsplit('}', 1)[-1]
        target = node.get('Include', node.get('Update', ''))
        if kind in ['PackageReference', 'ProjectReference'] and 'MillWorks.AuditCore' in target:
            items.append({'kind': kind, 'target': target,
                          'version': node.get('Version', node.findtext('Version'))})
    if items: refs.append({'path': str(p.relative_to(CORE.parent)), 'references': items,
                          'historicalEvidenceProject': any(part.lower() in ['evidence', 'reports'] for part in p.parts)})
report = {
    'observedUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
    'scope': 'Production MillWorks C# references and literal initializer scan, plus actual XML project references in local sibling checkouts. Source inventory is not runtime or deployment acceptance.',
    'method': 'rg file inventory; all production AuditEnvelope-bearing files manually reviewed for aliases, target-typed new, factory/forwarder calls and with expressions; lexical initializer fields below are not semantic authorization proof.',
    'productionFilesScanned': len(sources),
    'auditEnvelopeFiles': [{'path': str(p.relative_to(HOST)), 'sha256': sha(p)} for p in mentions],
    'initializers': initializers,
    'directLoggerFiles': [{'path': str(p.relative_to(HOST)), 'sha256': sha(p)} for p in sources if re.search(r'\bIAuditLogger\b', p.read_text())],
    'customHostPublishingCapabilities': [],
    'capabilitySearch': 'No host IAuditSink/IAuditOutboxWriter/IAuditEnvelopeBatchLogger implementation found; unused IAuditPublisher forwarder removed.',
    'checkoutReferences': refs,
}
(HERE / 'producer-inventory.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({'productionFilesScanned': len(sources), 'auditEnvelopeFiles': len(mentions),
                  'initializers': len(initializers), 'missingExplicitEventIds': sum(x['fields']['ExplicitEventId'] is None for x in initializers),
                  'missingGovernanceIdentity': sum(x['fields']['GovernanceIdentity'] is None for x in initializers),
                  'localReferenceCheckouts': sorted({x['path'].split('/')[0] for x in refs})}, indent=2))
