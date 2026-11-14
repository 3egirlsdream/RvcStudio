"""Authenticated store packages; decrypted model/index data stays in memory."""
import base64, hashlib, io, json, re, struct, zipfile
from pathlib import Path
from Crypto.Cipher import AES
from Crypto.Hash import SHA256
from Crypto.PublicKey import RSA
from Crypto.Signature import pkcs1_15

def verify(envelope, public_key):
    payload=base64.b64decode(envelope['payload'],validate=True)
    if len(payload)>1048576: raise ValueError('商城签名数据过大')
    key=RSA.import_key(public_key)
    if key.size_in_bits()<2048: raise ValueError('商城公钥无效')
    pkcs1_15.new(key).verify(SHA256.new(payload),base64.b64decode(envelope['signature'],validate=True))
    return json.loads(payload)

def load(payload, public_key):
    if not public_key: raise PermissionError('商城信任配置尚未启用')
    m=verify(payload['manifest'],public_key); g=verify(payload['grant'],public_key); s=verify(payload['snapshot'],public_key)
    mid,rev,account=payload['model_id'],payload['revision'],payload['account_id']
    if any(not re.fullmatch(r'[A-Za-z0-9-]{1,64}',v) for v in (mid,rev)): raise ValueError('商城标识无效')
    if (m.get('kind')!='modelManifest' or m.get('schema')!=1 or m['modelId']!=mid or m['revision']!=rev or
        g.get('kind')!='modelGrant' or g['accountId']!=account or g['modelId']!=mid or g['revision']!=rev or
        s.get('kind')!='membership' or s['accountId']!=account): raise PermissionError('商城授权与账号不匹配')
    if m['access'] not in ('Member','Free') or (m['access']=='Member' and not(s['isMember'] or s['canManageStore'])): raise PermissionError('当前会员状态无法使用该音色')
    key=bytearray(base64.b64decode(payload['content_key'],validate=True)); plain=bytearray()
    try:
        if len(key)!=32 or hashlib.sha256(key).hexdigest().upper()!=g['keySha256']: raise ValueError('商城密钥无效')
        path=Path(payload['package_path'])
        if path.stat().st_size!=m['size'] or path.stat().st_size>2147483648: raise ValueError('商城模型包大小不符')
        digest=hashlib.sha256()
        with path.open('rb') as source:
            for chunk in iter(lambda:source.read(4194304),b''): digest.update(chunk)
        if digest.hexdigest().upper()!=m['sha256']: raise ValueError('商城模型包校验失败')
        with path.open('rb') as source:
            def exact(n):
                data=source.read(n)
                if len(data)!=n: raise ValueError('商城模型包不完整')
                return data
            if exact(6)!=b'RVCS1\n': raise ValueError('商城包格式无效')
            n=struct.unpack('<I',exact(4))[0]
            if not 0<n<=4096: raise ValueError('商城包头无效')
            h=json.loads(exact(n)); prefix=base64.b64decode(h['noncePrefix'],validate=True); size=h['plainSize']
            if h['modelId']!=mid or h['revision']!=rev or h['chunkSize']!=4194304 or len(prefix)!=8 or not 0<size<=min(2147483648,m['modelSize']+m['indexSize']+65536): raise ValueError('商城包头与授权不符')
            number=0
            while len(plain)<size:
                n=struct.unpack('<I',exact(4))[0]
                if n!=min(4194304,size-len(plain)): raise ValueError('商城分块无效')
                cipher=AES.new(key,AES.MODE_GCM,nonce=prefix+struct.pack('>I',number))
                cipher.update(f'RVCS1|{mid}|{rev}|{number}|{size}'.encode())
                plain.extend(cipher.decrypt_and_verify(exact(n),exact(16))); number+=1
            if source.read(1): raise ValueError('商城模型包包含多余数据')
        with zipfile.ZipFile(io.BytesIO(plain)) as archive:
            if sorted(archive.namelist())!=['model.index','model.pth']: raise ValueError('商城包包含非法条目')
            if archive.getinfo('model.pth').file_size!=m['modelSize'] or archive.getinfo('model.index').file_size!=m['indexSize']: raise ValueError('模型解压大小不符')
            model_bytes=archive.read('model.pth'); index_bytes=archive.read('model.index')
        import torch, faiss, numpy as np
        checkpoint=torch.load(io.BytesIO(model_bytes),map_location='cpu',weights_only=True)
        index=faiss.deserialize_index(np.frombuffer(index_bytes,dtype=np.uint8)); version=checkpoint.get('version','v1')
        if version!=m['version'] or int(checkpoint['config'][-1])!=m['sampleRate'] or index.d!=(256 if version=='v1' else 768) or index.ntotal<=0: raise ValueError('商城模型与索引类型不匹配')
        return dict(checkpoint=checkpoint,index=index,model_id=mid,revision=rev,manifest=m)
    finally:
        key[:]=b'\0'*len(key); plain[:]=b'\0'*len(plain)
