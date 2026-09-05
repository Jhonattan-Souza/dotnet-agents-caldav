#!/usr/bin/env python3
"""Create disposable Nextcloud or Baikal fixtures alongside the Radicale lane."""
import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import time

from infra import docker, request


IMAGES = {
    'nextcloud': 'nextcloud:34.0.3-apache@sha256:b97df9e0e1ee3c8c6cc009cb3f12ddce915d624d543b3bb93882025fe323a407',
    'baikal': 'ckulka/baikal:0.10.1-apache@sha256:658e9582f8d418392b4dfe7e816835b003694c8cb54ed0ba598eb5727b58fe22',
}


BAIKAL_SETUP = r'''<?php
require '/var/www/baikal/vendor/autoload.php';
$base = '/var/www/baikal/';
$user = getenv('RFC_USERNAME');
$pass = getenv('RFC_PASSWORD');
$realm = 'BaikalDAV';
$config = [
  'system' => [
    'configured_version' => '0.10.1', 'timezone' => 'UTC',
    'card_enabled' => false, 'cal_enabled' => true,
    'dav_auth_type' => 'Basic', 'auth_realm' => $realm,
    'admin_passwordhash' => md5('admin:'.$realm.':'.$pass),
    'failed_access_message' => 'fixture authentication failure',
    'base_uri' => '', 'invite_from' => ''
  ],
  'database' => ['backend' => 'sqlite', 'sqlite_file' => $base.'Specific/db/db.sqlite',
    'mysql_host' => '', 'mysql_dbname' => '', 'mysql_username' => '',
    'mysql_password' => '', 'pgsql_host' => '', 'pgsql_dbname' => '',
    'pgsql_username' => '', 'pgsql_password' => '', 'encryption_key' => '']
];
file_put_contents($base.'config/baikal.yaml', Symfony\Component\Yaml\Yaml::dump($config));
if (!is_dir($base.'Specific/db')) mkdir($base.'Specific/db');
$db = new PDO('sqlite:'.$base.'Specific/db/db.sqlite');
$db->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
$db->exec(file_get_contents($base.'Core/Resources/Db/SQLite/db.sql'));
$db->prepare('INSERT INTO users(username,digesta1) VALUES (?,?)')->execute([$user,md5($user.':'.$realm.':'.$pass)]);
$q = $db->prepare('INSERT INTO principals(uri,email,displayname) VALUES (?,?,?)');
$q->execute(['principals/'.$user, $user.'@example.invalid', 'RFC disposable']);
$q->execute(['principals/'.$user.'/calendar-proxy-read', null, null]);
$q->execute(['principals/'.$user.'/calendar-proxy-write', null, null]);
echo "Configured disposable Baikal SQLite fixture\n";
'''


def up(root, server, telemetry_root):
    root.mkdir(parents=True, exist_ok=False)
    root.chmod(0o700)
    telemetry = json.loads((telemetry_root / 'infra-private.json').read_text())
    run = 'caldav-rfc-' + secrets.token_hex(4)
    name = run + '-' + server
    state = dict(run=run, username='rfctest', password=secrets.token_hex(20),
                 api_key=telemetry['api_key'], dashboard=telemetry['dashboard'],
                 otlp=telemetry['otlp'], containers=[name], profile=None,
                 server=server, principal_exists=True)
    path = root / 'infra-private.json'
    def save():
        path.write_text(json.dumps(state, indent=2))
        path.chmod(0o600)
    save()
    if server == 'nextcloud':
        values = {'NEXTCLOUD_ADMIN_USER': state['username'],
                  'NEXTCLOUD_ADMIN_PASSWORD': state['password'],
                  'SQLITE_DATABASE': 'rfcfixture',
                  'NEXTCLOUD_TRUSTED_DOMAINS': '127.0.0.1 localhost',
                  'NEXTCLOUD_INIT_HTACCESS': 'true'}
        state['home_path'] = '/remote.php/dav/calendars/rfctest/'
        dav_path = '/remote.php/dav/'
    else:
        values = {'RFC_USERNAME': state['username'], 'RFC_PASSWORD': state['password']}
        state['home_path'] = '/dav.php/calendars/rfctest/'
        dav_path = '/dav.php/'
    env_file = root / 'container-private.env'
    env_file.write_text(''.join(k + '=' + v + '\n' for k, v in values.items()))
    env_file.chmod(0o600)
    save()
    docker('run', '-d', '--name', name, '--label', 'caldav.rfc.owner=' + run,
           '--cpus', '4', '--memory', '2g', '-p', '127.0.0.1::80',
           '--env-file', str(env_file), IMAGES[server])
    env_file.unlink()
    state['url'] = 'http://' + docker('port', name, '80/tcp')
    state['caldav_url'] = state['url'] + dav_path
    save()
    if server == 'baikal':
        subprocess.run(['docker', 'exec', '--user', 'www-data', '-i', name, 'php'],
                       input=BAIKAL_SETUP, text=True, check=True)
    body = '<D:propfind xmlns:D="DAV:"><D:prop><D:resourcetype/></D:prop></D:propfind>'
    for _ in range(240):
        try:
            if request(state, 'PROPFIND', state['home_path'], body, {'Depth': '0'})[0] == 207:
                break
        except OSError:
            pass
        time.sleep(0.5)
    else:
        raise RuntimeError(server + ' did not become ready; inspect its owned container.')
    public = {k: state[k] for k in ['run', 'url', 'caldav_url', 'dashboard', 'otlp',
                                    'containers', 'profile', 'server']}
    public['image'] = IMAGES[server]
    (root / 'infra.json').write_text(json.dumps(public, indent=2))
    print(json.dumps(public))


def fresh_nextcloud_user(root, parent):
    state=json.loads((parent/'infra-private.json').read_text())
    if state.get('server')!='nextcloud' or len(state['containers'])!=1:
        raise ValueError('Use the original owned Nextcloud fixture manifest as the parent.')
    container=state['containers'][0]
    owner=docker('inspect','--format','{{index .Config.Labels "caldav.rfc.owner"}}',container)
    if owner!=state['run']:
        raise ValueError('The Nextcloud container does not belong to this fixture.')
    root.mkdir(parents=True,exist_ok=False)
    root.chmod(0o700)
    username='rfc'+secrets.token_hex(4)
    password=secrets.token_hex(20)
    subprocess.run(['docker','exec','--user','www-data','--env','NC_PASS',container,
                    'php','occ','user:add','--password-from-env','--display-name','RFC disposable fixture',username],
                   env=dict(os.environ,NC_PASS=password),check=True,capture_output=True,text=True)
    state.update(username=username,password=password,home_path='/remote.php/dav/calendars/'+username+'/',
                 containers=[],parent_fixture=str(parent))
    manifest=root/'infra-private.json'
    manifest.write_text(json.dumps(state,indent=2))
    manifest.chmod(0o600)
    public=dict(server='nextcloud',image=IMAGES['nextcloud'],parent_fixture=str(parent),
                fixture_change='fresh disposable user; default server creation limits unchanged',
                explicit_scope_required=True)
    (root/'infra.json').write_text(json.dumps(public,indent=2))
    print(json.dumps(public))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('server', choices=sorted(IMAGES))
    parser.add_argument('root', type=Path)
    parser.add_argument('--telemetry-root', type=Path)
    parser.add_argument('--fresh-user-from',type=Path)
    args = parser.parse_args()
    if args.fresh_user_from:
        if args.server!='nextcloud':
            parser.error('Fresh-user setup applies only to Nextcloud.')
        fresh_nextcloud_user(args.root.resolve(),args.fresh_user_from.resolve())
    elif args.telemetry_root:
        up(args.root.resolve(), args.server, args.telemetry_root.resolve())
    else:
        parser.error('Provide --telemetry-root for a new container or --fresh-user-from for a new Nextcloud user.')
