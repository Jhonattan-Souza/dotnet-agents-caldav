#!/usr/bin/env python3
"""Harness regressions; temporary Git repositories and stub external executables."""
import contextlib
import asyncio
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import stat
import xml.etree.ElementTree as ET
from types import SimpleNamespace
from unittest.mock import patch

from build_manifest import capture, finalize, load_builds, source_identity, benchmark_inputs, verify_process_inputs, runtime_files
from aggregate import load_traces, schema_observations, validate_gates
from infra import radicale_config, save_private_state, verify, expected_counts
import cleanup as cleanup_module
import profile as profiling
import benchmark
import driver
import gates
from functional import verify_occurrence_fixture
from edges import record_scale

HARNESS = Path(__file__).resolve().parent
ASSEMBLIES = ['DotnetAgents.CalDav.Mcp.dll', 'DotnetAgents.CalDav.Core.dll']


def git(repository, *args):
    return subprocess.check_output(['git', '-C', str(repository), *args], stderr=subprocess.DEVNULL).decode().strip()


def repository(root):
    repo = root / 'repository'
    repo.mkdir()
    git(repo, 'init', '--quiet')
    git(repo, 'config', 'user.name', 'Harness Test')
    git(repo, 'config', 'user.email', 'harness@example.invalid')
    git(repo, 'config', 'commit.gpgsign', 'false')
    git(repo, 'remote', 'add', 'origin', 'https://example.invalid/harness.git')
    (repo / 'Directory.Packages.props').write_text('original')
    commit(repo)
    return repo


def commit(repo):
    git(repo, 'add', '.')
    git(repo, 'commit', '--quiet', '-m', 'Test fixture')


def executable(path, contents):
    path.write_text('#!/bin/sh\n' + contents + '\n')
    path.chmod(0o755)


def runtime_fixture(directory,label):
    names=ASSEMBLIES+['Example.Dependency.dll']
    for name in names:
        (directory/name).write_bytes(('test-only-'+label+name).encode())
    deps=dict(runtimeTarget=dict(name='test'),targets={'test':{'app':{'runtime':{name:{} for name in names}}}})
    (directory/'DotnetAgents.CalDav.Mcp.deps.json').write_text(json.dumps(deps))
    (directory/'DotnetAgents.CalDav.Mcp.runtimeconfig.json').write_text('{}')


class BuildIdentityTests(unittest.TestCase):
    def test_otlp_comparison_rejects_different_dependency_closures(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);builds={}
            for label in ['baseline','candidate']:
                directory=root/label;directory.mkdir();runtime_fixture(directory,'same')
                (directory/'Example.Dependency.dll').write_text(label)
                hashes={name:hashlib.sha256((directory/name).read_bytes()).hexdigest() for name in ASSEMBLIES}
                builds[label]=dict(source=dict(sha=label),assembly_sha256=hashes,
                                   runtime_files_sha256=runtime_files(directory/ASSEMBLIES[0]))
            with self.assertRaisesRegex(RuntimeError,'same build'):
                benchmark_inputs(builds,root/'baseline'/ASSEMBLIES[0],root/'candidate'/ASSEMBLIES[0],True)
    def test_prepared_revisions_survive_later_commits_and_working_directory_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = repository(root)
            expected = {}
            for label in ['baseline', 'candidate']:
                if label == 'candidate':
                    (repo / 'Directory.Packages.props').write_text('candidate')
                    commit(repo)
                expected[label] = git(repo, 'rev-parse', 'HEAD')
                manifest = root / (label + '-build.json')
                capture(repo, manifest)
                assembly_dir = root / label
                assembly_dir.mkdir()
                runtime_fixture(assembly_dir,label)
                finalize(repo, manifest, assembly_dir)
            (repo / 'Directory.Packages.props').write_text('later revision')
            commit(repo)
            with contextlib.chdir(root):
                builds = load_builds(root)
            self.assertNotEqual(expected['baseline'], expected['candidate'])
            for label in expected:
                self.assertEqual(expected[label], builds[label]['source']['sha'])
                self.assertFalse(builds[label]['source']['dirty'])
            baseline = root / 'baseline' / ASSEMBLIES[0]
            candidate = root / 'candidate' / ASSEMBLIES[0]
            inputs = benchmark_inputs(builds, baseline, candidate)
            manifest = dict(baseline=str(baseline), candidate=str(candidate), compare_otlp=False, build_inputs=inputs)
            (root / 'paired-manifest.json').write_text(json.dumps(manifest))
            processes = [dict(label=label, assembly=value['assembly'],
                runtime_files_sha256=value['runtime_files_sha256'],
                sha256=value['assembly_sha256'][ASSEMBLIES[0]], core_sha256=value['assembly_sha256'][ASSEMBLIES[1]])
                for label, value in inputs.items()]
            verify_process_inputs(root, 'paired', processes, builds)
            with self.assertRaisesRegex(RuntimeError, 'prepared build'):
                benchmark_inputs(builds, candidate, baseline)
            processes[0]['assembly'] = str(candidate)
            with self.assertRaisesRegex(RuntimeError, 'declared benchmark input'):
                verify_process_inputs(root, 'paired', processes, builds)
            otlp = benchmark_inputs(builds, candidate, candidate, compare_otlp=True)
            self.assertEqual(expected['candidate'], otlp['baseline']['source']['sha'])
            self.assertEqual(otlp['baseline'], otlp['candidate'])
            dependency=root/'candidate'/'Example.Dependency.dll'
            original=dependency.read_bytes()
            dependency.write_bytes(b'stale dependency with unchanged first-party assemblies')
            with self.assertRaisesRegex(RuntimeError,'dependency set differs'):
                load_builds(root)
            with self.assertRaisesRegex(RuntimeError,'prepared build'):
                benchmark_inputs(builds,baseline,candidate)
            dependency.unlink()
            with self.assertRaisesRegex(RuntimeError,'Missing app-local runtime dependency'):
                runtime_files(candidate)
            dependency.write_bytes(original)
            (root / 'candidate' / ASSEMBLIES[0]).write_bytes(b'replaced binary')
            with self.assertRaisesRegex(RuntimeError, 'differs'):
                load_builds(root)

    def test_staged_and_untracked_changes_are_captured_and_mid_build_edits_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = repository(root)
            clean = source_identity(repo)
            (repo / 'Directory.Packages.props').write_text('staged dependency change')
            git(repo, 'add', 'Directory.Packages.props')
            (repo / 'added.cs').write_text('new source')
            dirty = source_identity(repo)
            self.assertTrue(dirty['dirty'])
            self.assertNotEqual(clean['working_tree_patch_sha256'], dirty['working_tree_patch_sha256'])
            self.assertIn('added.cs', dirty['untracked_file_sha256'])
            manifest = root / 'candidate-build.json'
            capture(repo, manifest)
            (repo / 'added.cs').write_text('changed during build')
            with self.assertRaisesRegex(RuntimeError, 'Source changed'):
                finalize(repo, manifest, root)


class CommandTests(unittest.TestCase):
    def test_proxy_propagates_child_status_after_forwarding_stderr(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ASSEMBLIES:
                (root / name).write_bytes(b'test-only placeholder')
            env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'])
            for code in [0, 17]:
                with self.subTest(code=code):
                    executable(root / 'dotnet', "echo 'child diagnostic' >&2\nexit " + str(code))
                    wire = root / ('wire-' + str(code) + '.jsonl')
                    result = subprocess.run([sys.executable, str(HARNESS / 'hermes_proxy.py'),
                        str(root / ASSEMBLIES[0]), str(wire)], env=env, input='', capture_output=True, text=True)
                    self.assertEqual(code, result.returncode, result.stderr)
                    self.assertIn('child diagnostic', result.stderr)
                    rows = [json.loads(line) for line in wire.read_text().splitlines()]
                    self.assertEqual('exit', rows[-1]['event'])
                    self.assertEqual(code, rows[-1]['code'])
                    self.assertTrue(any(row['event'] == 'stderr' and row['bytes'] > 0 for row in rows))

    def test_existing_hermes_wire_is_preserved_and_rejected_before_launch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            wire = root / 'hermes-wire-sanitized.jsonl'
            wire.write_text('previous attempt\n')
            for script, arguments in [('hermes.py', [str(root), 'missing.dll']),
                                      ('hermes_proxy.py', ['missing.dll', str(wire)])]:
                with self.subTest(script=script):
                    (root / 'yaml.py').write_text('')
                    (root / 'dotenv.py').write_text('def dotenv_values(path): return {}\n')
                    result = subprocess.run([sys.executable, str(HARNESS / script), *arguments],
                        env=dict(os.environ, PYTHONPATH=str(root)), capture_output=True, text=True)
                    self.assertNotEqual(0, result.returncode)
                    self.assertTrue('already exists' in result.stderr or 'FileExistsError' in result.stderr, result.stderr)
                    self.assertEqual('previous attempt\n', wire.read_text())

    def test_invalid_start_counts_fail_before_opening_infrastructure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for options in [('--samples', '7'), ('--samples', '1'), ('--samples', '0'),
                            ('--cohort-samples', '0'), ('--blocks', '-1'), ('--concurrency', '2'),
                            ('--cohort-samples', '12', '--samples', '12'),
                            ('--compare-otlp', '--no-otlp')]:
                with self.subTest(options=options):
                    result = subprocess.run([sys.executable, str(HARNESS / 'benchmark.py'), str(root),
                        'baseline.dll', 'candidate.dll', '--name', 'invalid', '--mode', 'start', *options],
                        capture_output=True, text=True)
                    self.assertEqual(2, result.returncode, result.stderr)
                    self.assertNotIn('Traceback', result.stderr)
                    self.assertEqual([], list(root.iterdir()))

    def test_start_count_boundary_reserves_five_warmup_snapshots(self):
        args = SimpleNamespace(blocks=1, samples=11, cohort_samples=11, mode='start',
                               topology='single_session', concurrency=1, compare_otlp=False, no_otlp=False)
        benchmark.validate_args(args)
        args.samples = args.cohort_samples = 12
        with self.assertRaisesRegex(ValueError, 'at most 11'):
            benchmark.validate_args(args)

    def test_package_uses_clean_commit_or_complete_dirty_candidate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = repository(root)
            scripts = repo / 'scripts'
            scripts.mkdir()
            for name in ['prepare-release-metadata.sh', 'verify-release-package.sh']:
                (scripts / name).write_text('exit 0\n')
            (repo / 'src').mkdir()
            (repo / 'src' / 'removed.cs').write_text('remove in candidate')
            commit(repo)
            bin_dir = root / 'bin'
            bin_dir.mkdir()
            executable(bin_dir / 'dotnet', 'exit 0')
            env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ['PATH'])
            for dirty in [False, True]:
                with self.subTest(dirty=dirty):
                    if dirty:
                        (repo / 'Directory.Packages.props').write_text('new dependency version')
                        git(repo, 'add', 'Directory.Packages.props')
                        (repo / 'src' / 'added.cs').write_text('untracked source')
                        (repo / 'src' / 'removed.cs').unlink()
                    output = root / ('dirty' if dirty else 'clean')
                    output.mkdir()
                    result = subprocess.run(['bash', str(HARNESS / 'package.sh'), str(output)],
                        cwd=repo, env=env, capture_output=True, text=True)
                    self.assertEqual(0, result.returncode, result.stderr)
                    clone = output / 'package-checkout'
                    self.assertEqual((repo / 'Directory.Packages.props').read_bytes(),
                                     (clone / 'Directory.Packages.props').read_bytes())
                    self.assertEqual(not dirty, (clone / 'src' / 'removed.cs').exists())
                    if dirty:
                        self.assertEqual('untracked source', (clone / 'src' / 'added.cs').read_text())

    def test_hermes_exit_status_reaches_the_shell_after_summary(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'source-home'
            source.mkdir()
            (source / 'config.yaml').write_text(json.dumps({'model': {'provider': 'openrouter', 'model': 'test'}}))
            (source / '.env').write_text('OPENROUTER_API_KEY=test-only-placeholder\n')
            (root / 'infra-private.json').write_text(json.dumps(dict(
                url='http://127.0.0.1:1', username='test', password='test-only', otlp='http://127.0.0.1:1')))
            stubs = root / 'stubs'
            stubs.mkdir()
            # Replace optional Hermes dependencies; this tests process handling, not the client SDK.
            (stubs / 'yaml.py').write_text('import json\nsafe_load = json.loads\nsafe_dump = json.dumps\n')
            (stubs / 'dotenv.py').write_text("def dotenv_values(path): return dict(line.split('=', 1) for line in path.read_text().splitlines())\n")
            env = dict(os.environ, PATH=str(stubs) + os.pathsep + os.environ['PATH'], PYTHONPATH=str(stubs))
            for exit_code in [0, 17]:
                with self.subTest(exit_code=exit_code):
                    executable(stubs / 'hermes', 'exit ' + str(exit_code))
                    result = subprocess.run([sys.executable, str(HARNESS / 'hermes.py'), str(root),
                        str(root / 'candidate.dll'), '--source-home', str(source)],
                        env=env, capture_output=True, text=True)
                    self.assertEqual(exit_code, result.returncode, result.stderr)
                    self.assertEqual(exit_code, json.loads(result.stdout)['exit_code'])


class EvidenceTests(unittest.TestCase):
    def test_schema_observations_require_correct_build_and_identical_workload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            builds={label:dict(assembly_sha256={ASSEMBLIES[0]:label},runtime_files_sha256={'dependency':label})
                    for label in ['baseline','candidate']}
            sources={label:dict(assemblySha256=label,tool='todos.query',payloadSha256='same-payload',
                runtimeFilesSha256=builds[label]['runtime_files_sha256'],
                samples=[dict(elapsedMilliseconds=1,allocatedBytes=100,gen0=0,gen1=0,gen2=0)])
                for label in builds}
            for label,source in sources.items():
                (root/('schema-'+label+'.json')).write_text(json.dumps(source))
            self.assertEqual(2,len(schema_observations(root,builds)))
            for field,value in [('assemblySha256','baseline'),('tool','calendar_entities.query'),
                                ('payloadSha256','different-payload'),('runtimeFilesSha256',{'dependency':'stale'})]:
                with self.subTest(field=field):
                    (root/'schema-candidate.json').write_text(json.dumps(dict(sources['candidate'],**{field:value})))
                    with self.assertRaises(RuntimeError):
                        schema_observations(root,builds)

    def test_named_trace_exports_merge_without_silent_conflicts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first, second = root / 'complete-traces.json', root / 'earlier-traces.json'
            trace = dict(trace_id='test-a', operation_ms=1)
            first.write_text(json.dumps([trace]))
            second.write_text(json.dumps([trace, dict(trace_id='test-b', operation_ms=2)]))
            self.assertEqual(2, len(load_traces([first, second])))
            second.write_text(json.dumps([dict(trace, operation_ms=3)]))
            with self.assertRaisesRegex(RuntimeError, 'Conflicting exports'):
                load_traces([first, second])

    def test_both_topologies_compare_build_results_and_retain_failures(self):
        for topology in ['single_session', 'processes']:
            for variation in ['equal', 'different', 'timeout']:
                with self.subTest(topology=topology, variation=variation), tempfile.TemporaryDirectory() as temporary:
                    root = Path(temporary)
                    (root / 'infra-private.json').write_text(json.dumps(dict(
                        url='http://127.0.0.1:1', username='test', password='test-only', otlp='http://127.0.0.1:1')))
                    args = SimpleNamespace(root=root, baseline=root/'baseline.dll', candidate=root/'candidate.dll',
                        name='check', mode='continue', no_otlp=True, compare_otlp=False, topology=topology,
                        concurrency=2, blocks=1, tools=['todos.query'], samples=2, sizes=[5], cohort_samples=5)

                    class FakeClient:
                        def __init__(self, assembly, env):
                            self.label = assembly.stem
                            self.process = SimpleNamespace(pid=1)
                            self.identity = {}

                        async def __aenter__(self):
                            return self

                        async def __aexit__(self, *args):
                            pass

                        async def request(self, *args):
                            return dict(result=dict(tools=[dict(name=tool) for tool in benchmark.TOOLS]))

                        async def call(self, tool, arguments):
                            failed = variation == 'timeout' and self.label == 'candidate' and 'cursor' in arguments
                            items = [2 if variation == 'different' and self.label == 'candidate' else 1]
                            record = dict(outcome='client_timeout' if failed else 'success', is_error=failed,
                                timed_out=failed, item_sha256=hashlib.sha256(json.dumps(items).encode()).hexdigest(),
                                items=1, elapsed_ms=1, cpu_ms=0, rss_bytes=1)
                            response = {} if failed else dict(result=dict(structuredContent=dict(
                                items=items, pagination=dict(nextCursor='test-cursor'))))
                            return response, record

                    with patch.object(benchmark, 'Client', FakeClient), patch.object(benchmark, 'load_builds', return_value={}), \
                            patch.object(benchmark, 'benchmark_inputs', return_value={}):
                        if variation == 'equal':
                            asyncio.run(benchmark.run(args))
                            self.assertTrue((root/'check-summary.json').exists())
                            summaries=json.loads((root/'check-summary.json').read_text())
                            for summary in summaries:
                                if topology=='single_session':
                                    self.assertIsNone(summary['mean_cpu_ms'])
                                    self.assertIn('overlapping',summary['cpu_measurement_scope'])
                                else:
                                    self.assertEqual(0,summary['mean_cpu_ms'])
                        else:
                            with self.assertRaisesRegex(RuntimeError, 'content/order|client_timeout'):
                                asyncio.run(benchmark.run(args))
                    rows = [json.loads(line) for line in (root/'check-samples.jsonl').read_text().splitlines()]
                    self.assertTrue(any(row['label'] == 'candidate' for row in rows))
                    if variation == 'timeout':
                        self.assertTrue(rows[-1]['timed_out'])


class ProfileTests(unittest.IsolatedAsyncioTestCase):
    async def test_start_profiles_bound_calls_and_retain_failures(self):
        for variation in ['success','busy','collector-start-failure']:
            with self.subTest(variation=variation), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary)
                (root/'infra-private.json').write_text(json.dumps(dict(
                    url='http://127.0.0.1:1',username='test',password='test',otlp='http://127.0.0.1:1')))
                clients=[];collectors=[]
                class FakeClient:
                    def __init__(self,*args):
                        self.calls=0;self.closed=False;self.identity={};self.process=SimpleNamespace(pid=1)
                        clients.append(self)
                    async def __aenter__(self): return self
                    async def __aexit__(self,*args): self.closed=True
                    async def call(self,*args):
                        self.calls+=1
                        outcome='busy' if variation=='busy' and self.calls==8 else 'success'
                        return dict(result=dict(structuredContent=dict(pagination=dict(nextCursor='cursor')))),dict(outcome=outcome)
                class Collector:
                    returncode=None
                    def terminate(self): self.returncode=-15
                    def kill(self): self.returncode=-9
                    async def wait(self):
                        if self.returncode is None: self.returncode=0
                        return self.returncode
                async def spawn(*args,**kwargs):
                    if variation=='collector-start-failure' and collectors: raise OSError('collector unavailable')
                    process=Collector();collectors.append(process);return process
                args=SimpleNamespace(root=root,assembly=root/'server.dll',profilers=root,name='capture',
                                     mode='start',tool='calendar_occurrences.query')
                with patch.object(profiling,'Client',FakeClient), patch.object(profiling.asyncio,'create_subprocess_exec',side_effect=spawn):
                    if variation=='success':
                        await profiling.run(args)
                    else:
                        with self.assertRaises((RuntimeError,OSError)):
                            await profiling.run(args)
                records=json.loads((root/'capture-profile-samples.json').read_text())
                identity=json.loads((root/'capture-profile-process.json').read_text())
                self.assertTrue(clients[0].closed)
                self.assertTrue(all(p.returncode is not None for p in collectors))
                self.assertEqual(variation=='success',identity['profile_completed'])
                if variation=='success':
                    self.assertEqual(10,clients[0].calls)
                    self.assertEqual(5,sum(not r['warmup'] for r in records))
                elif variation=='busy':
                    self.assertEqual('busy',records[-1]['outcome'])


class GateEvidenceTests(unittest.TestCase):
    def test_gate_runner_records_source_and_only_completes_all_steps(self):
        for failure in [None,'tests']:
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary)
                candidate=dict(source=dict(sha='candidate',dirty=False),runtime_files_sha256={'runtime':'hash'})
                calls=[]
                def command(argv,**kwargs):
                    name=gates.STEPS[len(calls)];calls.append(name)
                    return SimpleNamespace(returncode=1 if name==failure else 0)
                with patch.object(gates,'load_builds',return_value={'candidate':candidate}), \
                        patch.object(gates,'source_identity',return_value=candidate['source']), \
                        patch.object(gates,'runtime_files',return_value=candidate['runtime_files_sha256']), \
                        patch.object(gates.subprocess,'run',side_effect=command):
                    if failure:
                        with self.assertRaisesRegex(RuntimeError,'Gate failed'):
                            gates.run(root,'evidence')
                    else:
                        gates.run(root,'evidence')
                proof=json.loads((root/'evidence/gate-source.json').read_text())
                self.assertEqual(failure is None,proof['completed'])
                self.assertEqual(gates.STEPS if failure is None else gates.STEPS[:4],calls)
                if failure:
                    with self.assertRaises(RuntimeError): gates.verify_gate_identity(root/'evidence',candidate)
                else:
                    gates.verify_gate_identity(root/'evidence',candidate)

    def test_complete_gates_are_required_before_aggregation(self):
        manifest=json.loads((HARNESS.parents[1]/'test-suite-manifest.json').read_text())
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            candidate=dict(source=dict(sha='candidate',dirty=False),runtime_files_sha256={'runtime':'hash'})
            proof=dict(completed=True,source=candidate['source'],source_after=candidate['source'],
                runtime_files_sha256=candidate['runtime_files_sha256'],steps=[dict(name=name,exit_code=0) for name in gates.STEPS])
            (root/'gate-source.json').write_text(json.dumps(proof))
            for item in manifest['artifacts']:
                trx=ET.Element('TestRun')
                results=ET.SubElement(trx,'Results')
                ET.SubElement(results,'UnitTestResult',executionId='execution',testId='test',testName='test',outcome='Passed')
                definition=ET.SubElement(ET.SubElement(trx,'TestDefinitions'),'UnitTest',id='test')
                ET.SubElement(definition,'TestMethod',className=item.get('requiredResult',{}).get('className','Synthetic.Tests'))
                ET.SubElement(ET.SubElement(trx,'TestEntries'),'TestEntry',executionId='execution',testId='test')
                ET.SubElement(ET.SubElement(trx,'ResultSummary',outcome='Completed'),'Counters',total='1',executed='1',passed='1')
                (root/item['trx']).write_bytes(ET.tostring(trx))
                if 'coveragePrefix' in item:
                    for form in ['cobertura','opencover']:
                        (root/(item['coveragePrefix']+'.coverage.'+form+'.test.xml')).write_text('<coverage/>')
            coverage=root/'coverage-report';coverage.mkdir()
            (coverage/'Cobertura.xml').write_text('<coverage line-rate="1" branch-rate="1"/>')
            validate_gates(root,candidate)
            for field,value in [('source',dict(sha='old',dirty=False)),
                                ('source_after',dict(sha='candidate',dirty=True)),
                                ('runtime_files_sha256',{'runtime':'stale'}),('completed',False)]:
                with self.subTest(field=field):
                    (root/'gate-source.json').write_text(json.dumps(dict(proof,**{field:value})))
                    with self.assertRaises(RuntimeError): validate_gates(root,candidate)
            (root/'gate-source.json').write_text(json.dumps(proof))
            strict=root/'strict-preconditions.trx';saved=strict.read_bytes();strict.unlink()
            with self.assertRaises(RuntimeError): validate_gates(root,candidate)
            strict.write_bytes(saved)
            core=root/'main-core.trx';original=core.read_bytes()
            for counter in ['failed','notExecuted','warning']:
                with self.subTest(counter=counter):
                    trx=ET.fromstring(original);trx.find('.//Counters').set(counter,'1')
                    core.write_bytes(ET.tostring(trx))
                    with self.assertRaises(RuntimeError): validate_gates(root,candidate)
            core.write_bytes(original)
            (coverage/'Cobertura.xml').write_text('<coverage line-rate="0.1" branch-rate="1"/>')
            with self.assertRaises(RuntimeError): validate_gates(root,candidate)


class PersistedEvidenceTests(unittest.TestCase):
    def test_failed_scale_is_recorded_and_rejected(self):
        records=[]
        record_scale(records.append,dict(outcome='success'),corpus_resources=600)
        with self.assertRaisesRegex(RuntimeError,'Scale query failed'):
            record_scale(records.append,dict(outcome='limit_exhausted',is_error=True),corpus_resources=6000)
        self.assertEqual('limit_exhausted',records[-1]['outcome'])
        self.assertEqual(6000,records[-1]['corpus_resources'])

    def test_occurrence_checks_reject_noops_and_wrong_instances(self):
        target='20260909T120000Z'
        def fixture(verb):
            lines=['BEGIN:VCALENDAR','BEGIN:VEVENT','UID:fixture','DTSTART:20260905T120000Z',
                   'DTEND:20260905T130000Z','RRULE:FREQ=DAILY;COUNT=3']
            if verb!='initial': lines.append('RDATE:'+target)
            if verb=='exclude': lines.append('EXDATE:'+target)
            lines.append('END:VEVENT')
            if verb in ['cancel','restore_cancellation']:
                lines+=['BEGIN:VEVENT','UID:fixture','RECURRENCE-ID:'+target]
                if verb=='cancel': lines.append('STATUS:CANCELLED')
                lines.append('END:VEVENT')
            return ('\r\n'.join(lines+['END:VCALENDAR',''])).encode()
        previous='initial'
        for verb in ['add','exclude','restore_exclusion','cancel','restore_cancellation']:
            with self.subTest(verb=verb):
                data=fixture(verb)
                verify_occurrence_fixture(data,verb,'fixture')
                with self.assertRaises(RuntimeError): verify_occurrence_fixture(fixture(previous),verb,'fixture')
                with self.assertRaises(RuntimeError):
                    verify_occurrence_fixture(data.replace(target.encode(),b'20260910T120000Z'),verb,'fixture')
                previous=verb


class StartupCleanupTests(unittest.IsolatedAsyncioTestCase):
    async def test_failed_negotiation_reaps_the_child_and_reader_tasks(self):
        for kind,error in [('legacy',AssertionError),('malformed',TypeError),
                           ('timeout',TimeoutError),('cancelled',asyncio.CancelledError)]:
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary)
                server=root/'dotnet'
                server.write_text('#!'+sys.executable+'\n'+'''import json, pathlib, sys, time
kind=pathlib.Path(sys.argv[1]).stem
if kind in ('timeout','cancelled'):
    time.sleep(60)
else:
    for line in sys.stdin:
        request=json.loads(line)
        result=[] if kind=='malformed' else {'supportedVersions':['legacy'], 'capabilities':{'tools':{}}}
        print(json.dumps({'jsonrpc':'2.0','id':request['id'],'result':result}),flush=True)
''')
                server.chmod(0o755)
                client=driver.Client(root/(kind+'.dll'),dict(os.environ))
                timeout=0.1 if kind=='timeout' else 45
                with patch.object(driver.shutil,'which',return_value=str(server)), \
                        patch.object(driver,'REQUEST_TIMEOUT_SECONDS',timeout), \
                        patch.object(driver,'SHUTDOWN_TIMEOUT_SECONDS',0.1):
                    task=asyncio.create_task(client.__aenter__())
                    if kind=='cancelled':
                        while not hasattr(client,'reader'):
                            await asyncio.sleep(0.001)
                        task.cancel()
                    with self.assertRaises(error):
                        await task
                self.assertIsNotNone(client.process.returncode)
                self.assertTrue(client.reader.done())
                self.assertTrue(client.stderr.done())
                self.assertEqual({},client.pending)


class InfrastructureTests(unittest.TestCase):
    def test_cleanup_accepts_the_custom_seed_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);counts=dict(events=2,todos=2,archive=0)
            (root/'corpus.json').write_text(json.dumps(dict(resources=counts)))
            (root/'infra-private.json').write_text('{}')
            (root/'hermes-isolated').mkdir()
            def remove_owned(path):
                (path/'infra-private.json').unlink()
                (path/'cleanup.json').write_text(json.dumps(dict(complete=True)))
            self.assertEqual(counts,expected_counts(root))
            with patch.object(cleanup_module,'verify',return_value=counts),patch.object(cleanup_module,'down',side_effect=remove_owned):
                cleanup_module.cleanup(root)
            self.assertFalse((root/'hermes-isolated').exists())
            self.assertEqual(counts,json.loads((root/'cleanup.json').read_text())['final_corpus_before_container_removal'])

    def test_private_host_parent_protects_nonroot_container_credentials(self):
        for mask in [0o022,0o077]:
            with self.subTest(umask=mask), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary)
                previous=os.umask(mask)
                try:
                    state=dict(username='test',password='test-only')
                    config=radicale_config(root,state)
                    manifest=root/'infra-private.json'
                    save_private_state(manifest,state)
                finally:
                    os.umask(previous)
                self.assertEqual(0o700,stat.S_IMODE(config.parent.stat().st_mode))
                self.assertEqual(0o600,stat.S_IMODE(manifest.stat().st_mode))
                self.assertEqual(0o755,stat.S_IMODE(config.stat().st_mode))
                self.assertEqual(0o644,stat.S_IMODE((config/'users').stat().st_mode))

    def test_corpus_verification_rejects_same_count_content_and_membership_changes(self):
        state=dict(username='test',corpus_etags={
            'events':{'/test/events/0000.ics':'"event-seed"'},
            'todos':{'/test/todos/0000.ics':'"todo-seed"'},'archive':{}})
        observed={name:dict(values) for name,values in state['corpus_etags'].items()}
        def response(state,method,path,*args):
            name=path.rstrip('/').split('/')[-1]
            xml=ET.Element('{DAV:}multistatus')
            for href,etag in {path:None,**observed[name]}.items():
                item=ET.SubElement(xml,'{DAV:}response')
                ET.SubElement(item,'{DAV:}href').text=href
                if etag:
                    ET.SubElement(ET.SubElement(item,'{DAV:}prop'),'{DAV:}getetag').text=etag
            return 207,ET.tostring(xml),{}
        with patch('infra.request',side_effect=response):
            self.assertEqual(dict(events=1,todos=1,archive=0),verify(state))
            observed['events']['/test/events/0000.ics']='"edited-content"'
            with self.assertRaisesRegex(RuntimeError,'content or membership changed'):
                verify(state)
            observed['events']={'/test/events/replacement.ics':'"event-seed"'}
            with self.assertRaisesRegex(RuntimeError,'content or membership changed'):
                verify(state)
            with self.assertRaisesRegex(RuntimeError,'manifest is missing'):
                verify(dict(username='test'))


if __name__ == '__main__':
    unittest.main()
