MyFramework：ResourceRef 资源引用凭证设计

ResourceManager 加载资源时，没有直接把 UnityEngine.Object 返回给业务层，而是返回一个 ResourceRef<T>。

这个类很小，但它承担了资源引用生命周期管理的核心逻辑。

# 一、代码
ResourceRef<T> 的实现如下：
```C#
public class ResourceRef<T> : ClassObject where T : UObject
{
	protected T mResource;                  // 引用的资源
	protected long mToken;                  // 引用凭证,一般不允许外部直接访问
	public override void resetProperty()
	{
		base.resetProperty();
		mResource = null;
		mToken = 0;
	}
	public void setResource(T res)
	{
		mResource = res;
		if (mResource == null)
		{
			logError("resource is null");
			return;
		}
		mToken = mResourceManager.addReference(mResource);
	}
	public bool isValid() { return mResource != null; }
	public T getResource() { return mResource; }
	public long getToken() { return mToken; }
	// 在UN_CLASS时自动被调用
	public override void destroy()
	{
		base.destroy();
		if (mResource == null)
		{
			logError("resource is null");
			return;
		}
		mResourceManager.removeReference(mResource, ref mToken);
	}
	// 对当前资源新创建一个引用对象出来,用于使多个地方对同一个资源拥有生命周期所有权
	public ResourceRef<T> copyRef()
	{
		CLASS(out ResourceRef<T> newObjRef).setResource(mResource);
		return newObjRef;
	}
}
```
业务层拿到的不是裸资源，而是：

```C#
ResourceRef<Texture> refTex;
```

真正使用时再取资源：

```C#
Texture tex = refTex.getResource();
```

释放时也不是直接卸载资源，而是释放引用对象：

```C#
mResourceManager.unload(ref refTex);
```

# 二、加载时加引用

同步加载资源时，ResourceManager 会先加载出真实资源：

```C#
T res = mAssetBundleLoader.loadAsset<T>(name);
```

资源不为空时，再创建 ResourceRef<T>：

```C#
CLASS(out ResourceRef<T> resRef).setResource(res);
return resRef;
```
`setResource()` 里会调用：

```C#
mToken = mResourceManager.addReference(mResource);
```

也就是说，只要业务层拿到一个 `ResourceRef<T>`，资源系统内部就会增加一份引用凭证。

# 三、token 不是简单计数

`ResourceManager` 中不是只保存一个整数引用计数，而是保存 `token` 集合：

```C#
protected Dictionary<int, HashSet<long>> mReferenceTokenList = new();
protected Dictionary<int, UObject>       mInstanceIDToUObject = new();
```

增加引用时：

```C#
public long addReference(UObject res)
{
	long token = ++mTokenSeed;
	int instanceID = res.GetInstanceID();
	mInstanceIDToUObject.TryAdd(instanceID, res);
	if (!mReferenceTokenList.getOrAddNew(instanceID).Add(token))
	{
		logError("添加资源引用凭证失败:" + token);
	}
	return token;
}
```

移除引用时：

```C#
public void removeReference(UObject res, ref long token)
{
	if (!mReferenceTokenList.TryGetValue(res.GetInstanceID(), out var list) || !list.Remove(token))
	{
		logError("移除资源引用凭证失败,可能是重复移除一个资源:" + token);
	}
	token = 0;
}
``` 

这里的关键是：

```
1、每个 ResourceRef 都有自己的 token
2、同一个资源可以有多个 token
3、释放时只移除当前 ResourceRef 的 token
4、token 清空后可以检测重复释放 
```

如果只用一个整数引用计数，重复释放很难定位。

使用 token 后，如果同一个 `ResourceRef` 被重复释放，第二次移除 `token` 就会失败，并打印错误。

# 四、tokenSeed 放在 ResourceManager

`mTokenSeed` 放在 `ResourceManager` 中：

```C#
protected static long mTokenSeed;
```

代码注释里写得很清楚：

不能放在 `ResourceRef<T>` 中，
因为每个模板类型都有一个静态变量，
这样就不能保证同一个资源的引用凭证在不同模板类型中是唯一的。
这是一个容易忽略的细节。

如果写成：

```C#
public class ResourceRef<T>
{
	private static long mTokenSeed;
}
```

那么这些类型会各自有一份静态变量：

```C#
ResourceRef<Texture>.mTokenSeed
ResourceRef<Sprite>.mTokenSeed
ResourceRef<UObject>.mTokenSeed
```

同一个资源可能通过不同泛型类型包装。

如果 `token` 分散在不同泛型类里生成，就可能出现重复 `token`。

所以 `token` 生成必须放在统一的 `ResourceManager` 中。

# 五、为什么用 InstanceID

引用表的 Key 使用的是：

```C#
res.GetInstanceID()
```

不是直接用 `UnityEngine.Object`。

代码注释说明了原因：

```
1、UObject 重载了 ==，
2、外部卸载 UObject 后可能出现 GetHashCode 不变，
3、但引用资源为空的问题，
4、所以使用 GetInstanceID 作为 Key。
```

Unity 的 `Object` 和普通 C# 对象不完全一样。

它有自己的生命周期。

资源被 Unity 销毁后，C# 引用还可能存在，但 == null 的行为已经被 Unity 重载。

如果直接拿 `UObject` 当 Dictionary Key，后续判断会变得不稳定。

用 `GetInstanceID()` 做索引，逻辑更明确。

# 六、释放时不立刻卸载

释放 `ResourceRef<T>` 时，只是移除 `token`。

```C#
mResourceManager.removeReference(mResource, ref mToken);
```

真正卸载资源不是在这里立即完成。

`ResourceManager.update()` 会定时检查引用表：

```C#
protected const float CHECK_REF_INTERVAL = 3.0f;
```

检查逻辑是：

```C#
foreach (var item in mReferenceTokenList)
{
	if (item.Value.isEmpty())
	{
		if (willRemoveList == null)
		{
			LIST(out willRemoveList);
		}
		willRemoveList.add(item.Key);
	}
}
```

发现某个资源的 token 集合为空后，再统一卸载：

```C#
foreach (int id in willRemoveList)
{
	mInstanceIDToUObject.Remove(id, out UObject item);
	mReferenceTokenList.Remove(id);
	unloadInternal(item);
}
```

这样做有两个好处：

```
1、资源释放和真实卸载解耦
2、避免同一帧频繁加载和卸载
3、业务层只负责释放引用。
```

资源系统决定什么时候真正卸载资源。

# 七、copyRef 的意义

`ResourceRef<T>` 提供了：

```C#
public ResourceRef<T> copyRef()
{
	CLASS(out ResourceRef<T> newObjRef).setResource(mResource);
	return newObjRef;
}
```

它不是简单复制对象引用。

它会创建一个新的 `ResourceRef<T>`，并重新调用 `setResource()`。

这意味着：

```
1、同一个资源
2、新的 `ResourceRef`
3、新的 `token`
4、独立生命周期
```

适合这种情况：

```
1、一个资源加载后，需要交给多个模块使用
2、每个模块都应该独立释放自己的引用
3、最后一个引用释放后，资源才允许卸载
```

如果只是把同一个 `ResourceRef<T>` 传给多个地方，就会出现所有权不清楚的问题。

一个模块释放后，其他模块可能还在使用。

`copyRef()` 让多个持有者拥有独立引用。

# 八、不是裸资源所有权

如果业务层直接拿 `Texture`、`Sprite`、`Prefab`，资源系统无法知道谁还在使用它。

`ResourceRef<T>` 的作用是把资源使用权显式化。

```
拿到 `ResourceRef`
    表示持有一份资源引用
释放 `ResourceRef`
    表示归还这份资源引用
```

资源本体可以被多个地方共享。

引用凭证属于每个持有者。

这个设计比裸传资源更适合框架统一管理资源生命周期。

# 九、和对象池配合

`ResourceRef<T>` 继承自 `ClassObject`。

它本身也是池化对象。

创建时：

```C#
CLASS(out ResourceRef<T> resRef)
```

释放时：

```C#
UN_CLASS(ref res);
```

释放过程会走：

```
destroy
    ↓
removeReference
    ↓
resetProperty
    ↓
回收到 ClassPool
```

`destroy()` 负责移除资源引用。

`resetProperty()` 负责清空自身字段。

这和 MyFramework 的对象池规则保持一致。

# 十、精巧点

`ResourceRef<T>` 精巧的地方主要有四个。

1. 引用不是 int，而是 token 集合
可以检测重复释放，也能让每个持有者有独立凭证。

2. tokenSeed 不放在泛型类中
避免不同 ResourceRef<T> 类型各自产生重复 token。

3. 使用 InstanceID 追踪 Unity Object
避免 Unity Object 重载 == 后带来的 Dictionary Key 问题。

4. copyRef 创建独立引用
同一个资源可以交给多个模块使用，每个模块释放自己的引用。

总结

`ResourceRef<T>` 的设计不是简单包一层资源对象。

它解决的是资源所有权问题。

核心流程是：

```
加载资源
    ↓
创建 ResourceRef
    ↓
ResourceManager 生成 token
    ↓
业务层持有 ResourceRef
    ↓
释放 ResourceRef
    ↓
移除 token
    ↓
所有 token 清空后资源进入卸载流程
```

这个设计让资源生命周期从“谁拿着裸对象”变成“谁持有引用凭证”。

在 MyFramework 这种同时支持 `AssetDatabase`、`AssetBundle`、异步加载、子资源、下载和卸载的资源系统里，这层引用凭证非常关键。