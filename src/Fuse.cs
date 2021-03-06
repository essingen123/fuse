//using UnityEngine; // ### 1
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Text;

/* A real-time comet stream plugin for unity.
 * For unity search for the 5x ### and change.
 * For usage scroll down to Main() method.
 */
public class Fuse { // : MonoBehaviour { // ### 2
	public static Fuse instance;
	public static string host = "localhost";//"fuse.rupy.se";
	public int port = 8000;//80;

	private readonly object sync = new System.Object();
	private Thread thread;
	private Queue<string> input, output;
	private Socket pull, push;
	private bool connected, first = true, alive = true;
	private string salt;

	private IPEndPoint remote;
	private BufferedStream stream;
	
	public static void Log(string message) {
		//Debug.Log(message); // uncomment ### 3
		Console.WriteLine(message); // comment ### 3
	}
	
	public Fuse() {
		//Start();
	}
	
	void Awake() {
		// ### 4
		// Also to debug multiplayer it helps if you check:
		// Edit -> Project Settings -> Player -> Resolution & Presentation -> Run In Background
		//Application.runInBackground = true;
		//DontDestroyOnLoad(gameObject);
	}
	
	public void Host(string host) {
		Fuse.host = host;
		bool policy = true;

		//policy = Security.PrefetchSocketPolicy(host, port); // not needed for most cases ### 5
		
		if(!policy)
			throw new Exception("Policy (" + host + ":" + port + ") failed.");
		//Log(host);
		IPAddress address = Dns.GetHostEntry(host).AddressList[host.Equals("localhost") ? 1 : 0];
		remote = new IPEndPoint(address, port);
		
		Log(host + " " + address);
		
		Connect();
	}

	public static int Ping(string host) {
		Fuse fuse = new Fuse();
		fuse.Host(host);
		fuse.Connect();
		int time = Environment.TickCount;
		fuse.Push("ping");
		return Environment.TickCount - time;
	}

	void Connect() {
		first = true;
		push = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		push.NoDelay = true;
		push.Connect(remote);
	}

	void Start() {
		Log("Start");

		instance = this;
		
		input = new Queue<string>();
		output = new Queue<string>();
		
		thread = new Thread(PushAsync);
		thread.Start();
	}

	public void Pull(string salt) {
		this.salt = salt;
		
		pull = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		pull.NoDelay = true;
		pull.Connect(remote);

		//String text = "GET /pull?salt=" + salt + " HTTP/1.1\r\nHost:" + host + "\r\nHead:less\r\n\r\n";
		String text = "GET /pull?salt=" + salt + " HTTP/1.1\r\nAccept:text/event-stream\r\nHost:" + host + "\r\nHead:less\r\n\r\n";
		
		stream = new BufferedStream(new NetworkStream(pull));

		pull.Send(Encoding.UTF8.GetBytes(text));

		Thread thread = new Thread(PullAsync);
		thread.Start();

		connected = true;
	}

	void OnDestroy() {
		// This is called between scene switches.
		// Make sure you don't have multiple instances 
		// laying around in prefabs and such!
		freeResources();
	}

	void OnApplicationQuit() {
		// TODO: Why doesn't this work always?
		//freeResources();
	}

	private void freeResources() {
		alive = false;
	
		lock(thread)
			Monitor.Pulse(thread);
		
		if(push != null)
			push.Disconnect(false);
	
		if(pull != null)
			pull.Disconnect(false);
	}

	private void PushAsync() {
		while(alive) {
			try {
				String message = null;

				lock(output) {
					if(output.Count > 0)
						message = output.Dequeue();
				}

				while(message != null) {
					Push(message);

					/* Here you can act on async push responses */

					lock(output) {
						if(output.Count > 0)
							message = output.Dequeue();
						else
							message = null;
					}
				}
			}
			catch(Exception e) {
				Log(e.Message);
			}
			finally {
				lock(thread)
					Monitor.Wait(thread);
			}
		}
	}

	public void Async(string data) {
		if(salt == null) {
			Log("Login or register first.");
			return;
		}
        
		lock(output)
			output.Enqueue(data);
			
		lock(thread)
			Monitor.Pulse(thread);
	}

	public string Push(string data) {
		lock(sync) {
			byte[] body = new byte[1024];

			if(salt != null)
				data = data.Substring(0, 4) + '|' + salt + data.Substring(4, data.Length - 4);

			String text = "GET /push?data=" + data + " HTTP/1.1\r\nHost: " + host + "\r\n";

			if(first) {
				text += "Head: less\r\n\r\n"; // enables TCP no delay
				first = false;
			}
			else
				text += "\r\n";

			push.Send(Encoding.UTF8.GetBytes(text));
			int read = push.Receive(body);
			text = Encoding.UTF8.GetString(body, 0, read);

			if(text.Length == 0) {
				Connect();

				text = "GET /push?data=" + data + " HTTP/1.1\r\nHost: " + host + "\r\n";
			
				if(first) {
					text += "Head: less\r\n\r\n"; // enables TCP no delay
					first = false;
				}
				else
					text += "\r\n";

				push.Send(Encoding.UTF8.GetBytes(text));
				read = push.Receive(body);
				text = Encoding.UTF8.GetString(body, 0, read);
			}

			int header = text.IndexOf("Content-Length:");
			int EOL = text.IndexOf("\r\n", header);
			int content = text.IndexOf("\r\n\r\n");
			int length = Int32.Parse(text.Substring(header + 15, EOL - (header + 15)));
			int count = read - (content + 4);

			int total = count;

			if(content + 4 + count > text.Length) // UTF-8
				total = text.Length - (content + 4);

			text = text.Substring(content + 4, total);
			
			if(length == count) {
				return text;
			}
			else {
				do {
					read = push.Receive(body);
					text += Encoding.UTF8.GetString(body, 0, read);
					count += read;
				}
				while (count < length);

				return text;
			}
		}
	}

	public string[] Read() {
		if(!connected)
			return null;
		
		if(input.Count > 0) {
			string[] messages = new string[input.Count];

			lock(input) {	
				for(int i = 0; i < messages.Length; i++) {
					messages[i] = input.Dequeue();
				}
			}
			
			return messages;
		}
		
		return null;
	}

	private string Line(Stream input) {
		MemoryStream stream = new MemoryStream();
		
		while(true) {
			int a = input.ReadByte();

			if(a == '\r') {
				int b = input.ReadByte();

				if(b == '\n') {
					return Encoding.UTF8.GetString(stream.ToArray());
				} else if(b > -1) {
					stream.WriteByte((byte) a);
					stream.WriteByte((byte) b);
				}
			} else if(a > -1) {
				stream.WriteByte((byte) a);
			}
		}
	}

	private void PullAsync() {
		StringBuilder builder = new StringBuilder();
		Boolean append = false, length = true;

		while(alive) {
			String line = Line(stream);

			if(append) {
				if(length) {
					length = false;
				}
				else {
					builder.Append(line);

					int index = builder.ToString().IndexOf('\n');

					while(index > -1) {
						String message = builder.ToString().Substring(0, index);

						if(message.Length > 0) {
							if(message.StartsWith("data:")) {
								message = message.Substring(6);
							}
							lock(input) {
								input.Enqueue(message);
							}
						}

						builder.Remove(0, index + 1);
						index = builder.ToString().IndexOf('\n');
					}

					length = true;
				}
			}
			else if(line.Length == 0) { // read all headers
				append = true;
			}
			
			if(line.Equals("0")) {
				alive = false;
			}
		}
	}

	// ------------- PROTOCOL  -------------

	public string Time() { // millisec from 1970
		return EasyPush("time")[2];
	}

	/* Anonymous user.
	 * You need to:
	 * - store both key and id.
	 * - set and get name (unique) or nick 
	 * manually if you need lobby.
	 */
	public string[] User() {
		return EasyUser("", "", "");
		// TODO: store both key and id
		//string key = user[3];
		//string id = user[4];
	}
	
	// Returns key to be stored.
	public string User(string name) {
		return EasyUser(name, "", "")[3];
	}
	
	public void User(string name, string pass) {
		User(name, pass, "");
	}

	public void User(string name, string pass, string mail) {
		EasyUser(name, Hash(pass + name.ToLower()), mail);
	}

	private string[] EasyUser(string name, string hash, string mail) {
		string[] user = Push("user|" + name + "|" + hash + "|" + mail).Split('|');

		if(user[1].Equals("fail")) {
			throw new Exception(user[2]);
		}

		this.salt = user[2];
		return user;
	}

	public string SignIdKey(string id, string key) {
		return Sign(id, key);
	}

	public string SignNameKey(string name, string key) {
		return Sign(name, key);
	}

	public string SignNamePass(string name, string pass) {
		return Sign(name, Hash(pass + name.ToLower()));
	}
	
	private string Sign(string user, string hide) {
		string[] salt = Push("salt|" + user).Split('|');
		
		if(salt[1].Equals("fail")) {
			throw new Exception(salt[2]);
		}
		
		string[] sign = Push("sign|" + salt[2] + "|" + Hash(hide + salt[2])).Split('|');

		if(sign[1].Equals("fail")) {
			throw new Exception(sign[2]);
		}

		return salt[2];
	}

	public bool Game(string game) {
		return BoolPush("game|" + game);
	}

	public bool Room(String type, int size) {
		return BoolPush("room|" + type + "|" + size);
	}

	public string[] ListRoom() {
		string list = Push("list|room");

		if(list.StartsWith("list|fail")) {
			Log(list);
			return null;
		}

		if(list.Length > 15)
			return list.Substring(15).Split(';'); // from 'list|done|room|'
		else
			return null;
	}

	public string[] ListItem(string type) { // type can be room or user
		string list = Push("list|" + type + "|item");

		if(list.StartsWith("list|fail")) {
			Log(list);
			return null;
		}

		Log(list);

		if (list.Length > 20)
			return list.Substring(20).Split(';'); // from 'list|done|xxxx|item|'
		else
			return null;
	}
	
	public string[] ListUser(string type) {
		string list = Push("list|user|" + type);

		if(list.StartsWith("list|fail")) {
			Log(list);
			return null;
		}

		if(list.Length > 15)
			return list.Substring(16 + type.Length).Split(';'); // from 'list|done|user|xxxx'
		else
			return null;
	}
    
	public bool Join(string room) {
		return BoolPush("join|" + room);
	}
	
	public bool Join(string user, string info) {
		return BoolPush("join|" + user + "|" + info);
	}
	
	public bool Exit() {
		return BoolPush("exit");
	}
	
	public bool Poll(string user, string accept) {
		return BoolPush("poll|" + user + "|" + accept);
	}

	public void Play(string seed) {
		Async("play|" + seed);
	}
	
	public void Over(string data) {
		Async("over|" + data);
	}
	
	public void Save(string name, string json) {
		Save(name, json, "hard");
	}
	
	public void Save(string name, string json, string type) {
		EasyPush("save|" + Uri.EscapeDataString(name) + "|" + Uri.EscapeDataString(json) + "|" + type);
	}
	
	// No feedback on failure unless handled in AsyncPush() above!
	public void AsyncSave(string name, string json, string type) {
		Async("save|" + Uri.EscapeDataString(name) + "|" + Uri.EscapeDataString(json) + "|" + type);
	}
	
	public string Load(string name) {
		return Load(name, "data");
	}
	
	public string Load(string name, string type) {
		return EasyPush("load|" + Uri.EscapeDataString(name) + "|" + type)[2];
	}
	
	// Delete data
	public void Tear(string name, string type) {
		EasyPush("tear|" + Uri.EscapeDataString(name) + "|" + type);
	}
	
	public string Hard(string user, string name) {
		return EasyPush("hard|" + user + "|" + Uri.EscapeDataString(name))[2];
	}
	
	public string Item(string user, string name) {
		return EasyPush("item|" + user + "|" + Uri.EscapeDataString(name))[2];
	}
	
	public string Soft(string user, string name) {
		return EasyPush("soft|" + user + "|" + Uri.EscapeDataString(name))[2];
	}
	
	public void Pick(string salt) {
		Async("pick|" + salt);
	}
	
	/* tree should be either root, stem or leaf
	 * root -> whole server, excluding others rooms
	 * stem -> game lobby, including your own room
	 * leaf -> your room only
	 * if you prefix text with @name it will send 
	 * to that user if online; no matter where and 
	 * what tree is used.
	 */
	public void Chat(string tree, string text) {
		Async("chat|" + tree + '|' + Uri.EscapeDataString(text));
	}

	public void Send(string data) {
		Async("send|" + data);
	}
	
	public void Show(string data) {
		Async("show|" + data);
	}

	public void Move(string data) {
		Async("move|" + data);
	}

	private bool BoolPush(string data) {
		string[] push = EasyPush(data);

		if(push == null) {
			return false;
		}

		return true;
	}

	private string[] EasyPush(string data) {
		string[] push = Push(data).Split('|');
		
		if(push[1].Equals("fail")) {
			throw new Exception(push[2]);
		}
		
		return push;
	}

	public string Hash(string input) {
		HashAlgorithm algo = (HashAlgorithm) SHA256.Create();
		byte[] bytes = Encoding.UTF8.GetBytes(input);
		byte[] hash = algo.ComputeHash(bytes);
		StringBuilder sb = new StringBuilder();
		for(int i = 0; i < hash.Length; i++) {
			sb.Append(hash[i].ToString("X2"));
		}
		return sb.ToString().ToLower();
	}

	// ------------- INCOMING MESSAGES ---------
	
	void Update() { // FixedUpdate() if you read keys from another location
		// Check key presses and send packets
		
		// TODO
		
		// Read incoming packets and update world
	
		string[] received = Read();

		if(received != null) {
			for(int i = 0; i < received.Length; i++) {
				Log("Read: " + received[i]);
			}
		}
	}

	// ------------- EXAMPLE USAGE -------------
	
	public static void Main() {
		try {
			Log("Ping: " + Ping(host));
		
			string key = "TvaaS3cqJhQyK6sn";
			string salt = null;
			
			Fuse fuse = new Fuse();
			fuse.Start();
			fuse.Host(host);
			
			//Thread.Sleep(100);
			
			Log("Time: " + fuse.Time());
			
			// if no key is stored try
/*
			try {
				key = fuse.User("fuse");
				Log("User: " + key);
			}
			catch(Exception e) {
				Log(e.Message);
				return;
			}
*/
			//   then store name and key
			// otherwise
			//   get name and key

			//key = "F9hG7K7Jwe1SmtiQ";
			//key = "yt4QACtL2uzbyUTT";

			//if(key != null) {
				try {
					salt = fuse.SignNameKey("fuse", key);
				}
				catch(Exception e) {
					Log(e.Message);
					return;
				}
			//}

			Log("Sign: " + salt);

			if(salt != null) {
				fuse.Pull(salt);
				
				// Very important to sleep a bit here
				// Use coroutines to send fuse.Game("race");
				// in Unity. Or send it on the first "noop" in Update()!
				Thread.Sleep(100);
				
				fuse.Game("race");

				Thread.Sleep(100);
				
				Thread thread = new Thread(FakeLoop);
				thread.Start();
				
				Thread.Sleep(500);

				fuse.Chat("root", "hello");

				Thread.Sleep(500);

				Log("Room: " + fuse.Room("race", 4));

				Thread.Sleep(500);

				string[] list = fuse.ListRoom();

				if(list != null) {
					Log("List: " + list.Length);

					for(int i = 0; i < list.Length; i++) {
						string[] room = list[i].Split(',');
						Log("      " + room[0] + " " + room[1] + " " + room[2]);
					}
				}
				
				Thread.Sleep(500);

				fuse.Chat("root", "hello");
				
				Thread.Sleep(500);
				
				fuse.Send("white+0+0");
			}
		}
		catch(Exception e) {
			Log(e.ToString());
		}
	}
	
	// ------------- UPDATE EMULATION -------------
	static void FakeLoop() {
		while(true) {
			try {
				instance.Update();
			}
			finally {
				Thread.Sleep(10);
			}
		}
	}
}
